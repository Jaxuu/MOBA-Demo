using System.Collections.Generic;
using UnityEngine;
using MOBA.Core;
using MOBA.Components;

namespace MOBA.Skills
{
    /// <summary>
    /// 范围效果场：非指向性技能在指定落点生成的"场"，在持续时间内周期性对范围内的敌方单位结算效果。
    ///
    /// 定位：与 Projectile 对称的另一种【效果载体】。Projectile 是"点对点的延迟结算"，
    /// 本类是"面对象、可重复的延迟结算"。
    ///
    /// 职责边界：
    /// 1. 只负责"圈在哪、半径多大、活多久、每多久结算一次、圈里谁能被打"，不负责伤害/减速的具体规则
    ///    （一律交给 SkillEffectResolver）；
    /// 2. 不引用任何表现资源：白盒阶段的可见性由 OnDrawGizmos 提供（README 明确要求作用范围需白盒可视化），
    ///    阶段八的 VFX 只需在本对象下挂一个视觉子物体即可，逻辑一行不用改；
    /// 3. 不搜索目标、不做索敌节流——范围查询本身就是它的核心工作。
    ///
    /// 【为什么用代码创建而不是预制体】本类目前没有任何需要美术资源的部件（逻辑 + Gizmos 就够），
    /// 引入预制体只会多一个必须由工具维护的资产。阶段八要加视觉时，在 Spawn 里挂一个视觉子物体即可。
    /// </summary>
    [DisallowMultipleComponent]
    public class AreaEffectZone : MonoBehaviour
    {
        [Header("调试")]
        [Tooltip("在 Console 输出每次周期结算命中的单位数量。")]
        [SerializeField] private bool logZoneEvents = false;

        [Tooltip("是否跟随施法者移动（阶段八新增，由 SkillData.followCaster 在 Initialize 时写入）。\n" +
                 "true = 每帧把场同步到施法者位置（以自身为中心的持续 AOE，如 E 审判）；\n" +
                 "false = 场固定在释放时的落点。")]
        [SerializeField] private bool followCaster = false;

        /// <summary>
        /// 范围场的对象名。
        ///
        /// 【为什么用常量而不是 $"AreaEffectZone_{data.DisplayName}"】技能释放路径上有 GC 预算
        /// （计划书验收标准：技能释放瞬间 GC Alloc ≤ 1 KB），而字符串插值每次都会分配一个新 string。
        /// 对象名对排查没有帮助（Hierarchy 里同时存在多个场时，靠位置就能分辨），
        /// 用常量可以零成本地省掉这次分配。
        /// </summary>
        private const string ZoneObjectName = "AreaEffectZone";

        /// <summary>
        /// 范围查询结果缓冲区容量。预分配后复用，保证周期结算【0 GC Alloc】。
        /// 32 对"半径 3.5 米"而言非常宽裕（正常情况下一波兵 + 英雄也就 10 个左右单位，
        /// 且每个单位通常有 1~2 个碰撞体）。
        /// </summary>
        private const int MaxOverlapResults = 32;

        /// <summary>范围查询缓冲区（复用，不每次分配数组）。</summary>
        private readonly Collider[] overlapBuffer = new Collider[MaxOverlapResults];

        /// <summary>
        /// 本次 tick 已结算过的目标列表（复用）。
        /// 必要性：一个单位常常有多个碰撞体（身体 + 武器 + 拾取盒），
        /// 若不去重，同一 tick 内会对它结算多次——这是"范围技能伤害翻倍"的经典成因，且极难排查。
        /// </summary>
        private readonly List<ITargetable> appliedThisTick = new List<ITargetable>();

        /// <summary>施法者（效果来源与阵营判定的依据），可能为 null。</summary>
        private EntityBase source;

        /// <summary>施法者的索敌组件。用于复用全项目唯一的敌我规则，可能为 null。</summary>
        private TargetingComponent sourceTargeting;

        /// <summary>技能配置。</summary>
        private SkillData data;

        /// <summary>场消失的时刻（Time.time 基准）。</summary>
        private float endTime;

        /// <summary>下一次周期结算的时刻（Time.time 基准）。</summary>
        private float nextTickTime;

        /// <summary>是否已做过"一次性结算"（用于 areaDuration 或 tickInterval 为 0 的配置）。</summary>
        private bool hasAppliedOnce;

        /// <summary>是否已就"范围查询结果被截断"告警过。</summary>
        private bool hasWarnedBufferFull;

        /// <summary>当前作用半径（只读），供调试与测试断言使用；未初始化时为 0。</summary>
        public float EffectRadius => data != null ? data.EffectRadius : 0f;

        /// <summary>施法者（只读），供测试断言与调试使用。</summary>
        public EntityBase Source => source;

        /// <summary>
        /// 在指定落点生成一个范围场。
        /// 静态工厂而不是构造器：Unity 组件必须挂在 GameObject 上，工厂把"创建物体 + 挂组件 + 初始化"
        /// 三件事收在一处，避免调用方漏掉任何一步（尤其是"先 AddComponent 再 Initialize"的顺序）。
        /// </summary>
        /// <param name="source">施法者。</param>
        /// <param name="data">技能配置。</param>
        /// <param name="center">落点（世界坐标）。</param>
        /// <returns>生成的范围场；参数非法时返回 null（调用方据此决定是否广播释放事件）。</returns>
        public static AreaEffectZone Spawn(EntityBase source, SkillData data, Vector3 center)
        {
            if (source == null || data == null)
            {
                return null;
            }

            GameObject zoneObject = new GameObject(ZoneObjectName);
            zoneObject.transform.position = center;

            AreaEffectZone zone = zoneObject.AddComponent<AreaEffectZone>();

            // 必须先 AddComponent 再 Initialize：AddComponent 会立刻触发 Awake，
            // 而 Initialize 里要用到组件自身的字段（与项目「动态创建必须先挂齐组件再注入」的约定一致）。
            zone.Initialize(source, data, center);
            return zone;
        }

        /// <summary>
        /// 初始化范围场。由 Spawn 在 AddComponent 之后立即调用。
        /// </summary>
        private void Initialize(EntityBase newSource, SkillData newData, Vector3 center)
        {
            source = newSource;
            data = newData;

            // 用 GetComponent 直接取而不是读 newSource.Targeting 属性：
            // 属性返回的是 EntityBase 在它自己的 Awake 里缓存的引用，动态创建时可能尚未补全。
            sourceTargeting = newSource.GetComponent<TargetingComponent>();

            // 跟随开关来自配置：它是"这个技能长什么样"的一部分，因此属于数据层，而不是运行期决定。
            followCaster = newData.FollowCaster;

            transform.position = center;

            float now = Time.time;
            endTime = now + Mathf.Max(0f, newData.AreaDuration);

            // 周期为 0 时把下次结算时间设为"永不"，真正的"只结算一次"由 Update 的独立分支处理。
            nextTickTime = newData.TickInterval > 0f ? now + newData.TickInterval : float.MaxValue;
        }

        /// <summary>本范围场是否跟随施法者移动（只读），供调试与测试断言使用。</summary>
        public bool IsFollowingCaster => followCaster;

        /// <summary>
        /// 跟随同步：把场搬到施法者脚下。
        ///
        /// 【为什么不用"把场设为施法者的子物体"】那会让场的生命周期被父物体绑架——
        /// 施法者阵亡/被销毁时场会一起消失，而"持续 AOE 在施法者死后是否继续"是一个设计决策，
        /// 不该由一个层级关系顺带决定。手动同步位置把两件事解耦：
        /// 施法者失效时场停在原地继续走完剩余时长（当前设计），要改成"随施法者一起消失"
        /// 也只需在这里加一个分支。
        ///
        /// 【为什么不更新 y】场是贴地的作用区域，施法者根节点本身就在地面上，
        /// 因此直接取 x/z 即可；不取 y 可以避免施法者因为导航吸附而在 y 上抖动时，
        /// 让场的 Gizmos 跟着上下跳。
        /// </summary>
        private void FollowCasterIfNeeded()
        {
            if (!followCaster || source == null)
            {
                return;
            }

            Vector3 casterPosition = source.transform.position;
            transform.position = new Vector3(casterPosition.x, transform.position.y, casterPosition.z);
        }

        /// <summary>
        /// 生命周期推进：到期结算 → 超时回收。
        /// 用"绝对时间点比较"而不是累加 deltaTime（与 SkillComponent 前摇同一模式）：
        /// 后者在帧率波动或组件被禁用时会产生累积误差。
        /// </summary>
        private void Update()
        {
            if (data == null)
            {
                // 没有配置就无法结算。正常情况下 Initialize 一定被调用过，这里只做防御性回收。
                Destroy(gameObject);
                return;
            }

            float now = Time.time;

            // 跟随同步必须在结算【之前】：否则"施法者刚走进圈里"的那一帧，
            // 结算用的还是上一帧的旧位置，表现为"贴身放 E 却打不到人"。
            FollowCasterIfNeeded();

            // 情形一：一次性爆发（未配持续时长）——生成即结算一次并立刻回收。
            if (data.AreaDuration <= 0f)
            {
                if (!hasAppliedOnce)
                {
                    ApplyToTargetsInRadius();
                    hasAppliedOnce = true;
                }

                Destroy(gameObject);
                return;
            }

            // 情形二：有持续时长但没有周期（tickInterval <= 0）——只在生成时结算一次，之后仅"留场"。
            // 这个分支是给"纯表现型区域"预留的（例如只播动画不造成效果的地面标记）。
            if (data.TickInterval <= 0f)
            {
                if (!hasAppliedOnce)
                {
                    ApplyToTargetsInRadius();
                    hasAppliedOnce = true;
                }

                if (now >= endTime)
                {
                    Destroy(gameObject);
                }

                return;
            }

            // 情形三：持续 + 周期结算（W 减速圈走这条路径）。
            if (now >= nextTickTime)
            {
                ApplyToTargetsInRadius();

                // Mathf.Max(0.1f, ...) 兜住"周期被配成 0.01"的极端配置：
                // 那等于每帧结算一次，会瞬间打空目标血量且产生大量伤害事件。
                nextTickTime = now + Mathf.Max(0.1f, data.TickInterval);
            }

            if (now >= endTime)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 对范围内的所有敌方单位结算一次效果。
        ///
        /// 用 OverlapSphereNonAlloc + 预分配缓冲：周期结算在整场对局里会执行上百次，
        /// 用会分配数组的 OverlapSphere 会持续产生 GC 压力（README 要求战斗稳态不因技能产生持续 GC）。
        /// </summary>
        private void ApplyToTargetsInRadius()
        {
            Vector3 center = transform.position;

            int hitCount = Physics.OverlapSphereNonAlloc(
                center, data.EffectRadius, overlapBuffer, Physics.AllLayers, QueryTriggerInteraction.Ignore);

            if (hitCount >= overlapBuffer.Length && !hasWarnedBufferFull)
            {
                hasWarnedBufferFull = true;
                Debug.LogWarning(
                    $"[AreaEffectZone] {name} 单次范围查询的命中数达到缓冲区上限（{overlapBuffer.Length}），" +
                    "结果可能被截断（部分单位不会受伤）。若确实存在大量碰撞体重叠，请调大 MaxOverlapResults。", this);
            }

            appliedThisTick.Clear();

            int appliedCount = 0;

            for (int i = 0; i < hitCount; i++)
            {
                Collider candidateCollider = overlapBuffer[i];
                if (candidateCollider == null)
                {
                    continue;
                }

                // 用 GetComponentInParent 而不是 GetComponent：碰撞体通常挂在模型子节点上，
                // 而 EntityBase（ITargetable 实现方）位于根节点。
                ITargetable candidate = candidateCollider.GetComponentInParent<ITargetable>();
                if (candidate == null)
                {
                    continue;
                }

                // 敌我校验：只打敌方，友方与中立零影响（README 验收项）。
                if (!IsEnemy(candidate))
                {
                    continue;
                }

                // 目标合法性：复用全项目唯一入口（已包含"未销毁 + 激活 + 有生命 + 未死 + 可选中"五项）。
                if (!candidate.IsValidTarget)
                {
                    continue;
                }

                // 去重：同一单位可能被多个碰撞体命中（见 appliedThisTick 的注释）。
                if (ContainsApplied(candidate))
                {
                    continue;
                }

                appliedThisTick.Add(candidate);

                if (SkillEffectResolver.Apply(source, candidate, data))
                {
                    appliedCount++;
                }
            }

            if (logZoneEvents && appliedCount > 0)
            {
                Debug.Log(
                    $"[AreaEffectZone] {name}（半径 {data.EffectRadius:F1}）本 tick 命中 {appliedCount} 个敌方单位", this);
            }
        }

        /// <summary>判断某个目标是否已在本 tick 内结算过。</summary>
        private bool ContainsApplied(ITargetable candidate)
        {
            for (int i = 0; i < appliedThisTick.Count; i++)
            {
                if (ReferenceEquals(appliedThisTick[i], candidate))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 敌我校验。优先复用施法者的索敌组件（保证"能锁定"与"能被打"是同一套规则），
        /// 没有索敌组件时才退化为直接比较阵营——退化路径的"中立阵营排除"与
        /// CombatComponent / SkillComponent 逐条对齐，避免出现规则分叉。
        /// </summary>
        private bool IsEnemy(ITargetable candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (sourceTargeting != null)
            {
                return sourceTargeting.IsEnemy(candidate);
            }

            if (source == null)
            {
                return false;
            }

            return candidate.Team != TeamType.Neutral && candidate.Team != source.Team;
        }

        /// <summary>
        /// 白盒可视化：把作用范围画出来。
        /// 用 OnDrawGizmos 而不是 OnDrawGizmosSelected：README 要求"作用范围需要白盒可视化便于验证与调试"，
        /// 战斗中不应该需要先选中它才看得到。编辑模式下 data 为 null，因此不会画出任何东西。
        /// </summary>
        private void OnDrawGizmos()
        {
            if (data == null)
            {
                return;
            }

            // 外圈线框 = 精确的作用半径。
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, data.EffectRadius);

            // 内圈半透明实心 = 让"场"在地面上有存在感（顶视角下更容易判断敌人有没有进圈）。
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.12f);
            Gizmos.DrawSphere(transform.position, data.EffectRadius);
        }
    }
}

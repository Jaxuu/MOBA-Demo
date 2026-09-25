using System;
using UnityEngine;
using MOBA.Core;

namespace MOBA.Skills
{
    /// <summary>
    /// 弹道：承载"指向性技能从施法者飞向目标，命中时才结算"这一段时间与空间。
    ///
    /// 定位说明（重要）：本组件是【逻辑载体】，不是表现组件。
    /// 它的视觉（球体 / 拖尾）只是子物体或自身渲染器，**命中判定完全由本类的距离计算决定**，
    /// 不使用 Rigidbody / Collider / OnTriggerEnter。理由有三：
    ///   1. README 验收要求"命中与伤害结算严格一致"——同一帧、同一处代码做判定与结算，天然一致；
    ///   2. 不依赖物理矩阵与 Layer 配置，白盒阶段即可跑通，也不会污染索敌用的 OverlapSphere 结果；
    ///   3. 命中时机不受物理帧（FixedUpdate）与渲染帧错位的影响。
    ///
    /// 表现与逻辑分离：本类只广播 OnHit 事件，不引用任何特效 / 音效 / 动画资源。
    /// 阶段八的 VfxSpawner 订阅 OnHit 播放命中特效即可，逻辑一行不用改。
    ///
    /// 四种终止条件（缺一不可，否则会出现"弹道永生"或"命中不结算"）：
    ///   ① 命中数达上限（V1 恒为 1，即非穿透）；
    ///   ② 超出最大飞行距离；
    ///   ③ 超出最大存活时间；
    ///   ④ 目标中途被销毁（判空后飞至最后已知位置再回收，不结算伤害）。
    /// </summary>
    [DisallowMultipleComponent]
    public class Projectile : MonoBehaviour
    {
        [Header("调试")]
        [Tooltip("在 Console 输出每次命中的目标与伤害。")]
        [SerializeField] private bool logHitEvents = false;

        /// <summary>施法者（伤害归属与日志用），可能为 null。</summary>
        private EntityBase source;

        /// <summary>飞行目标（可能中途死亡 / 被销毁，因此每次访问都要做存活检查）。</summary>
        private ITargetable target;

        /// <summary>技能配置（飞行速度、命中半径、伤害等参数的唯一来源）。</summary>
        private SkillData data;

        /// <summary>
        /// 目标最后一次已知的位置。
        /// 作用：目标在飞行途中死亡或被销毁时，弹道仍要"飞完剩下的路"再回收——
        /// 直接原地消失会让玩家觉得"技能凭空没了"；而继续追踪一个已销毁的对象会抛异常。
        /// </summary>
        private Vector3 lastKnownTargetPosition;

        /// <summary>发射位置，用于"最大飞行距离"判定。</summary>
        private Vector3 launchPosition;

        /// <summary>发射时刻，用于"最大存活时间"判定。</summary>
        private float launchTime;

        /// <summary>最大存活时间（秒），由配置推算，用于兜住极端情况。</summary>
        private float maxLifetime;

        /// <summary>是否已回收，防止 Recycle 被重入（例如 OnHit 的订阅者在回调里销毁了弹道）。</summary>
        private bool hasRecycled;

        /// <summary>是否已就"配置了穿透但 V1 不支持"告警过。</summary>
        private bool hasWarnedPiercing;

        /// <summary>
        /// 命中事件：参数依次为（本弹道, 被命中的目标, 命中位置）。
        /// 供阶段八的表现层订阅播放命中特效与音效。只在【真的结算了效果】时广播。
        /// </summary>
        public event Action<Projectile, ITargetable, Vector3> OnHit;

        /// <summary>是否已回收（只读），供测试断言"命中后弹道确实被销毁"。</summary>
        public bool HasRecycled => hasRecycled;

        /// <summary>施法者（只读）。</summary>
        public EntityBase Source => source;

        /// <summary>当前飞行目标（只读，可能已失效）。</summary>
        public ITargetable Target => target;

        /// <summary>
        /// 初始化弹道。由 ProjectileSpawner 在生成后立即调用（先 AddComponent 再 Initialize，
        /// 与项目「动态创建的单位必须先挂齐组件再注入」的约定一致）。
        /// </summary>
        /// <param name="newSource">施法者。</param>
        /// <param name="newTarget">飞行目标。</param>
        /// <param name="newData">技能配置。</param>
        public void Initialize(EntityBase newSource, ITargetable newTarget, SkillData newData)
        {
            source = newSource;
            target = newTarget;
            data = newData;

            launchPosition = transform.position;
            launchTime = Time.time;

            // 初始"最后已知位置"：目标若在生成瞬间就已失效，就飞向一个空点并很快回收，不会抛异常。
            // 追踪点的 y 统一取发射点高度（见 ResolveAimPoint），保证弹道在胸口高度平飞。
            lastKnownTargetPosition = TryResolveTargetPosition(out Vector3 position)
                ? new Vector3(position.x, launchPosition.y, position.z)
                : launchPosition;

            // 最大存活时间 = 理论最长飞行时间 × 3 倍余量。
            // 为什么除了最大飞行距离还需要它：距离判定兜住"飞得太远"，但目标静止不动且因浮点误差
            // 始终判定不到命中时，距离也不会继续增长——两道保险缺一不可。
            float speed = newData != null ? Mathf.Max(0.01f, newData.ProjectileSpeed) : 1f;
            float travelBudget = newData != null ? newData.CastRange + newData.MaxTravelDistance : 20f;
            maxLifetime = Mathf.Max(1f, travelBudget / speed * 3f);
        }

        private void Update()
        {
            if (hasRecycled)
            {
                return;
            }

            if (data == null)
            {
                // 没有配置就没有飞行参数。正常情况下 Initialize 一定会被调用，
                // 走到这里说明生成流程被绕过，直接回收比每帧抛异常好。
                Recycle();
                return;
            }

            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            Vector3 aimPoint = ResolveAimPoint();
            Vector3 currentPosition = transform.position;

            float speed = Mathf.Max(0.01f, data.ProjectileSpeed);
            float step = speed * deltaTime;
            float distance = Vector3.Distance(currentPosition, aimPoint);

            // 命中判定：满足任一条件即判定命中。
            //  · distance <= 命中半径：常规命中；
            //  · distance <= step：本帧的位移足以越过判定点，若不提前判定，高速弹道会直接穿过去
            //    （tunnelling），表现为"明明瞄准了却打不到"。
            if (distance <= data.ProjectileRadius || distance <= step)
            {
                transform.position = aimPoint;
                HandleHit(aimPoint);
                return;
            }

            transform.position = Vector3.MoveTowards(currentPosition, aimPoint, step);

            // 让弹道朝向飞行方向（纯视觉，不影响任何判定）。
            Vector3 direction = aimPoint - currentPosition;
            if (direction.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.LookRotation(direction);
            }

            // 条件②：超出最大飞行距离。
            if ((transform.position - launchPosition).sqrMagnitude >= data.MaxTravelDistance * data.MaxTravelDistance)
            {
                Recycle();
                return;
            }

            // 条件③：超出最大存活时间。
            if (Time.time - launchTime >= maxLifetime)
            {
                Recycle();
            }
        }

        /// <summary>
        /// 命中处理：结算效果 → 广播事件 → 回收。
        /// </summary>
        /// <param name="hitPosition">命中位置（表现层播特效的锚点）。</param>
        private void HandleHit(Vector3 hitPosition)
        {
            // 命中瞬间重新解析目标：飞行途中目标可能已死亡 / 被销毁。
            // 那种情况下【不结算伤害】，但弹道仍要正常回收——这是 README 的明确验收项
            // （目标中途死亡时弹道安全回收，无空引用）。
            Vector3 liveTargetPosition;
            ITargetable liveTarget = TryResolveTargetPosition(out liveTargetPosition) ? target : null;

            if (liveTarget != null && SkillEffectResolver.Apply(source, liveTarget, data))
            {
                if (logHitEvents)
                {
                    Debug.Log(
                        $"[Projectile] {name} 命中 {DescribeTarget(liveTarget)}，结算 {data.EffectType} " +
                        $"（伤害 {data.Damage:F1}）", this);
                }

                OnHit?.Invoke(this, liveTarget, hitPosition);
            }

            if (data.MaxHitCount > 1 && !hasWarnedPiercing)
            {
                hasWarnedPiercing = true;
                Debug.LogWarning(
                    $"[Projectile] {data.name} 配置了穿透（maxHitCount = {data.MaxHitCount}），" +
                    "但 V1 只实现非穿透弹道（命中即回收）——穿透需要「锁定下一个目标」的续飞逻辑，" +
                    "属阶段六不做范围。当前已按非穿透处理。");
            }

            // 【为什么这里必须无条件回收】弹道抵达判定点后，下一帧"距离 <= 命中半径"依然成立，
            // 若继续飞行，就会每帧重复结算一次伤害——这是最容易被漏掉的一类重复伤害 bug。
            Recycle();
        }

        /// <summary>
        /// 回收弹道。幂等：重复调用只会销毁一次。
        /// </summary>
        private void Recycle()
        {
            if (hasRecycled)
            {
                return;
            }

            hasRecycled = true;

            // 断开事件订阅链：订阅方（表现层）可能持有本对象的引用，
            // 主动置空可避免"已销毁弹道仍被回调"的隐患。
            OnHit = null;

            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            hasRecycled = true;
            OnHit = null;
        }

        /// <summary>
        /// 解析目标的当前位置。目标失效（已销毁 / 已死亡 / 不可选中 / Transform 丢失）时返回 false。
        /// </summary>
        private bool TryResolveTargetPosition(out Vector3 position)
        {
            position = Vector3.zero;

            ITargetable liveTarget = target;
            if (liveTarget == null)
            {
                return false;
            }

            // 接口引用在 GameObject 被销毁后不会变成 null，必须先做存活检查再访问其属性。
            if (liveTarget is UnityEngine.Object unityObject && unityObject == null)
            {
                return false;
            }

            if (!liveTarget.IsValidTarget)
            {
                return false;
            }

            Transform targetTransform = liveTarget.TargetTransform;
            if (targetTransform == null)
            {
                return false;
            }

            position = targetTransform.position;
            return true;
        }

        /// <summary>
        /// 取本帧的追踪点：目标还活着就刷新"最后已知位置"，否则沿用上一次记录的位置飞完剩余路程。
        ///
        /// 【为什么要把 y 拉到发射点高度】目标位置是实体的根节点（贴地，y = 0），而发射点在胸口高度。
        /// 若直接朝根节点飞，弹道会从胸口一路扎到地面，顶视角下看起来像"钻地"；
        /// 统一到发射点高度后弹道是平飞的，视觉上才是"打在身上"。
        /// 命中判定用的就是这个追踪点，因此"看起来打到了"与"判定打到了"仍然是同一个位置。
        /// </summary>
        private Vector3 ResolveAimPoint()
        {
            if (TryResolveTargetPosition(out Vector3 position))
            {
                lastKnownTargetPosition = new Vector3(position.x, launchPosition.y, position.z);
            }

            return lastKnownTargetPosition;
        }

        /// <summary>生成目标的简短描述，仅用于日志。</summary>
        private static string DescribeTarget(ITargetable hitTarget)
        {
            if (hitTarget is Component component)
            {
                return $"{component.name}({hitTarget.Team})";
            }

            return $"ITargetable({hitTarget.Team})";
        }
    }
}

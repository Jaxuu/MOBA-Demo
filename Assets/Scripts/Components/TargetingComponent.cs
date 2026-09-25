using UnityEngine;
using MOBA.Core;

namespace MOBA.Components
{
    /// <summary>
    /// 索敌组件：负责"找到并保存一个合法目标"，不负责"追上它"或"打它"。
    ///
    /// 职责边界（对应计划书 3.2）：
    /// 1. 提供目标搜索（FindNearestEnemy）、目标设置（SetTarget）与清理（ClearTarget / ClearInvalidTarget）；
    /// 2. 不发起攻击（那是 CombatComponent 的事），不驱动移动（那是 FSM 与 MovementComponent 的事）；
    /// 3. 只依赖 ITargetable 接口，因此不关心目标是英雄、小兵还是防御塔。
    ///
    /// 性能约定（计划书 6.4）：FindNearestEnemy 内部会做一次物理范围查询，
    /// 必须按固定周期调用（例如 AI 的检测间隔），**不要放进每帧的 Update 里无条件调用**。
    /// </summary>
    [DisallowMultipleComponent]
    public class TargetingComponent : MonoBehaviour
    {
        [Header("索敌参数（通常由 EntityBase 从 EntityStatsData 注入）")]
        [Tooltip("索敌半径（单位）。由 EntityBase 注入 EntityStatsData.DetectionRange，此处仅作兜底默认值。")]
        [Min(0f)]
        [SerializeField] private float detectionRadius = 8f;

        [Tooltip("参与索敌检测的 Layer。默认全部层；若单位层划分清晰，收窄掩码可显著降低查询开销。")]
        [SerializeField] private LayerMask targetLayers = Physics.AllLayers;

        [Header("调试")]
        [Tooltip("在 Console 输出目标切换记录。小兵数量多时会比较吵，默认关闭。")]
        [SerializeField] private bool logTargetChanges = false;

        [Tooltip("选中该对象时，在 Scene 视图中绘制索敌范围与当前目标连线。")]
        [SerializeField] private bool drawTargetGizmos = true;

        /// <summary>本单位的实体身份入口（原始缓存），请通过 <see cref="Owner"/> 访问。</summary>
        private EntityBase owner;

        /// <summary>
        /// 实体身份入口（延迟解析）。
        /// 为什么要延迟解析：用代码动态创建单位时（MinionSpawner、自动化测试等）组件挂载顺序不可控，
        /// EntityBase 有可能晚于本组件被 AddComponent，那样 Awake 里缓存到的就是 null 且永远不会更新。
        /// 这里改成"首次访问时补一次查找"，成功后就一直命中缓存，运行期没有额外开销。
        /// </summary>
        private EntityBase Owner
        {
            get
            {
                if (owner == null)
                {
                    owner = GetComponent<EntityBase>();
                }

                return owner;
            }
        }

        /// <summary>是否已就"缺少 EntityBase"报过错误，防止每次索敌都刷日志。</summary>
        private bool hasReportedMissingOwner;

        /// <summary>
        /// 当前锁定的目标。null 表示没有目标。
        /// private set：外部只能通过 SetTarget / ClearTarget 改变目标，保证切换时一定会走校验与日志。
        /// </summary>
        public ITargetable CurrentTarget { get; private set; }

        /// <summary>是否持有目标。给 FSM 判断状态切换用，避免到处写 null 比较。</summary>
        public bool HasTarget => CurrentTarget != null;

        /// <summary>当前索敌半径（只读），供调试视图与 FSM 读取。</summary>
        public float DetectionRadius => detectionRadius;

        /// <summary>
        /// 缓存实体身份引用。
        /// 阵营必须从 EntityBase 统一读取，而不是在本组件里再放一个 TeamType 字段——
        /// 否则同一单位会出现两份阵营数据，迟早不一致。
        /// 这里只做一次尝试性缓存，不在 Awake 报错：动态创建时 EntityBase 可能后挂，
        /// 真正的缺失会在首次使用 <see cref="Owner"/> 时被报出（见 FindNearestEnemy）。
        /// </summary>
        private void Awake()
        {
            owner = GetComponent<EntityBase>();
        }

        /// <summary>
        /// 设置索敌半径。由 EntityBase 在初始化时用 EntityStatsData.DetectionRange 调用。
        /// </summary>
        /// <param name="radius">索敌半径，负值会被夹到 0。</param>
        public void SetDetectionRadius(float radius)
        {
            detectionRadius = Mathf.Max(0f, radius);
        }

        /// <summary>
        /// 判断给定目标是否为敌人。
        ///
        /// 规则（严格依据 README 2.2「单位仅可攻击敌对阵营目标，不可攻击友方、中立或已死亡单位」）：
        /// 1. 目标阵营为 Neutral → 【不是】敌人。中立单位（野怪、无归属单位）不属于任何一方的敌人；
        /// 2. 否则，阵营与本方不同即为敌人。
        ///
        /// 注意本规则是【单向的】：它约束的是"本单位可以打谁"。因此一个 Neutral 单位自身仍会
        /// 把非中立单位视为敌人——README 的约束对象是"目标"，而不是"攻击者"。
        /// V1 版本没有中立单位，此处的语义差异暂时不影响表现。
        ///
        /// 公开出来是为了让 CombatComponent 也能复用同一套判断，避免两处规则写得不一致。
        /// </summary>
        /// <param name="target">待判断的目标，允许为 null。</param>
        /// <returns>是敌人返回 true；target 为 null、已销毁、中立阵营、或与本方同阵营返回 false。</returns>
        public bool IsEnemy(ITargetable target)
        {
            if (target == null || Owner == null)
            {
                return false;
            }

            // 目标 GameObject 可能已被销毁，此时接口引用不会自动变成 null，必须先做存活检查。
            if (!IsAlive(target))
            {
                return false;
            }

            // 中立单位永远不是敌人。这条必须写在这里而不是各调用点：
            // IsEnemy 是 SetTarget / ClearInvalidTarget / FindNearestEnemy / CombatComponent 的共同入口，
            // 放在这里可一次性保证"小兵绝对不会把中立单位识别为敌人"。
            if (target.Team == TeamType.Neutral)
            {
                return false;
            }

            return target.Team != owner.Team;
        }

        /// <summary>
        /// 设置当前目标。
        /// 校验顺序：null → 已销毁 → 不合法（死亡/不可选中）→ 友方。任一不通过都不会改变当前目标。
        /// 注意：本方法刻意不接受友方目标。后续若要做治疗/增益类技能，需要另加重载或开关，
        /// 而不是把这里的判断放松——否则"不能攻击友方"这条验收标准会失去代码层面的保障。
        /// </summary>
        /// <param name="target">目标，传 null 等价于 ClearTarget()。</param>
        public void SetTarget(ITargetable target)
        {
            if (target == null)
            {
                ClearTarget();
                return;
            }

            if (!IsAlive(target))
            {
                Debug.LogWarning($"[TargetingComponent] {name} 试图锁定一个已被销毁的目标，已忽略。", this);
                return;
            }

            if (!target.IsValidTarget)
            {
                Debug.LogWarning($"[TargetingComponent] {name} 试图锁定一个无效目标（已死亡或不可选中），已忽略。", this);
                return;
            }

            if (!IsEnemy(target))
            {
                Debug.LogWarning($"[TargetingComponent] {name} 试图锁定友方目标，已忽略（不能攻击友方）。", this);
                return;
            }

            if (ReferenceEquals(CurrentTarget, target))
            {
                // 同一目标重复设置：直接返回，避免重复打印日志与无意义的属性写入。
                return;
            }

            CurrentTarget = target;

            if (logTargetChanges)
            {
                Debug.Log($"[TargetingComponent] {name} 锁定目标：{DescribeTarget(target)}", this);
            }
        }

        /// <summary>
        /// 清空当前目标。攻击被中断、目标逃出追击范围、单位死亡时调用。
        /// </summary>
        public void ClearTarget()
        {
            if (CurrentTarget == null)
            {
                return;
            }

            if (logTargetChanges)
            {
                Debug.Log($"[TargetingComponent] {name} 清除目标：{DescribeTarget(CurrentTarget)}", this);
            }

            CurrentTarget = null;
        }

        /// <summary>
        /// 校验当前目标是否仍然可用，不可用则清除。
        /// 典型调用时机：AI 检测周期的开头。因为 ITargetable 不暴露死亡事件，
        /// 所以采用"轮询校验"而不是"事件通知"，这也是把校验集中在本方法的原因——
        /// 调用方只需调一次，不必自己拼装 IsValidTarget + IsEnemy 两个条件。
        /// </summary>
        /// <returns>是否发生了清理（true 表示目标刚失效并被清除）。</returns>
        public bool ClearInvalidTarget()
        {
            if (CurrentTarget == null)
            {
                return false;
            }

            // 目标被销毁或失去可选中资格，或阵营发生变化（例如被策反）都视为失效。
            if (IsAlive(CurrentTarget) && CurrentTarget.IsValidTarget && IsEnemy(CurrentTarget))
            {
                return false;
            }

            ClearTarget();
            return true;
        }

        /// <summary>
        /// 在索敌半径内搜索最近的合法敌方目标。
        /// 筛选条件：实现了 ITargetable、阵营不同、IsValidTarget 为 true、Transform 有效。
        /// </summary>
        /// <returns>最近的合法敌人；找不到返回 null（调用方需判空）。</returns>
        public ITargetable FindNearestEnemy()
        {
            if (Owner == null)
            {
                if (!hasReportedMissingOwner)
                {
                    hasReportedMissingOwner = true;
                    Debug.LogError(
                        $"[TargetingComponent] {name} 上找不到 EntityBase，无法判断阵营，索敌功能不可用。" +
                        "请确保 EntityBase 与 TargetingComponent 挂在同一个 GameObject 上。", this);
                }
                return null;
            }

            if (detectionRadius <= 0f)
            {
                return null;
            }

            Vector3 origin = transform.position;

            // 注意：OverlapSphere 每次调用都会分配一个新数组（GC Alloc）。
            // 因此本方法只应在固定检测周期里调用；若后续单位数量上升导致 GC 压力明显，
            // 可替换为 Physics.OverlapSphereNonAlloc + 预分配缓冲，接口签名无需改变。
            Collider[] candidates = Physics.OverlapSphere(origin, detectionRadius, targetLayers, QueryTriggerInteraction.Ignore);

            ITargetable nearest = null;
            float nearestSqrDistance = float.MaxValue;

            for (int i = 0; i < candidates.Length; i++)
            {
                Collider candidateCollider = candidates[i];
                if (candidateCollider == null)
                {
                    continue;
                }

                // 用 GetComponentInParent 而不是 GetComponent：碰撞体通常挂在模型子节点上，
                // 而 EntityBase / ITargetable 实现位于根节点，向上查找才能稳定命中。
                ITargetable candidate = candidateCollider.GetComponentInParent<ITargetable>();
                if (candidate == null)
                {
                    continue;
                }

                // 过滤自身、友方、已死亡/不可选中目标。
                if (!IsEnemy(candidate) || !candidate.IsValidTarget)
                {
                    continue;
                }

                Transform candidateTransform = candidate.TargetTransform;
                if (candidateTransform == null)
                {
                    continue;
                }

                // 比较平方距离而不是 Distance：省掉每次循环的开方运算。
                float sqrDistance = (candidateTransform.position - origin).sqrMagnitude;
                if (sqrDistance < nearestSqrDistance)
                {
                    nearestSqrDistance = sqrDistance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 判断 ITargetable 引用背后的 Unity 对象是否仍然存活。
        /// 为什么必须单独判断：接口引用在 GameObject 被销毁后【不会】变成 null，
        /// 直接访问 target.TargetTransform 会抛 MissingReferenceException。
        /// 非 Unity 对象（例如单元测试里的纯 C# 桩）一律视为存活。
        /// </summary>
        private static bool IsAlive(ITargetable target)
        {
            if (target is UnityEngine.Object unityObject)
            {
                return unityObject != null;
            }

            return true;
        }

        /// <summary>
        /// 生成目标的简短描述，仅用于日志，避免在日志里直接打印接口对象（会输出类型名，信息量低）。
        /// </summary>
        private static string DescribeTarget(ITargetable target)
        {
            if (target == null)
            {
                return "null";
            }

            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }

        /// <summary>
        /// Scene 视图可视化：绘制索敌范围与当前目标连线。
        /// 用 OnDrawGizmosSelected 而不是 OnDrawGizmos：单位多时全部绘制会让视图不可读。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!drawTargetGizmos)
            {
                return;
            }

            // 红色半透明球 = 索敌半径，便于直观核对 DetectionRange 是否配置合理。
            Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, detectionRadius);

            if (CurrentTarget == null || !IsAlive(CurrentTarget))
            {
                return;
            }

            Transform targetTransform = CurrentTarget.TargetTransform;
            if (targetTransform == null)
            {
                return;
            }

            // 红色连线 + 小圈 = 当前锁定的目标。
            Gizmos.color = Color.red;
            Gizmos.DrawLine(transform.position + Vector3.up * 0.5f, targetTransform.position + Vector3.up * 0.5f);
            Gizmos.DrawWireSphere(targetTransform.position + Vector3.up * 0.5f, 0.3f);
        }
    }
}

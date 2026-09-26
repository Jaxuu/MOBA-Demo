using UnityEngine;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.Components
{
    /// <summary>
    /// 动画组件（表现层，阶段九）：把【逻辑状态】单向映射为 Animator 参数，是全项目
    /// **唯一允许引用 Animator 的组件**（README §3.4 硬性约束 1）。
    ///
    /// ============================ 它做什么 ============================
    /// <code>
    ///  逻辑事件（权威、只读订阅）              动画参数（纯视觉、可丢失）
    ///  ─────────────────────────────────────────────────────────────
    ///  MovementComponent.CurrentVelocity   ─▶  Speed (float)  平滑过渡，驱动 Idle ↔ Run
    ///  CombatComponent.OnAttackPerformed   ─▶  Attack (trigger)
    ///  SkillComponent.OnCastStarted        ─▶  Spell  (trigger)
    ///  HealthComponent.OnDied              ─▶  Die    (trigger)
    /// </code>
    ///
    /// ==================== 三条不可越过的边界 ====================
    /// ① **绝不反向回调伤害**：本类不订阅任何 Animation Event，也不提供任何"动画播到某一帧
    ///    就调用战斗方法"的通道。伤害结算只由 <c>CombatComponent</c>（距离 + CD + 目标合法性）
    ///    与 <c>SkillComponent</c>（CD + 蓝耗 + 施法距离 + 目标合法性）决定（README §3.4 约束 2）。
    ///    若美术给动画片段加了 Animation Event，它**只能**调用纯表现方法（特效 / 音效），
    ///    绝不允许调用 TakeDamage / Heal / 任何修改战斗数值的入口。
    /// ② **逻辑不等待动画**：攻击节奏由 <c>AttackData.AttackInterval</c> 决定，技能生效时刻由
    ///    <c>SkillData.CastTime</c> 决定，两者都与动画片段长度无关（README §3.4 约束 3）。
    ///    动画比逻辑长就被裁剪、比逻辑短就重复播，逻辑一行都不用改。
    /// ③ **可整体关闭**：把 <see cref="enableAnimation"/> 关掉（或删掉本组件 / 删掉 Animator），
    ///    对局结果与胜负完全一致（README §3.4 约束 4）。本类只写 Animator 参数，
    ///    不读也不写任何战斗字段。
    ///
    /// ==================== 为什么速度读的是"实际速度"而不是"配置速度" ====================
    /// <c>MovementComponent.Speed</c> 是 <c>NavMeshAgent.speed</c>（配置值，恒定不变），
    /// 拿它当动画参数会得到一个"永远在跑"的动画；<c>MovementComponent.CurrentVelocity</c> 是
    /// 代理本帧的真实速度向量，被减速、被眩晕、被前摇锁住、被拥堵顶住时都会自然趋近 0 ——
    /// 这正是动画需要的那条曲线。归一化时用的分母才是配置速度（见 ResolveSpeedReference）。
    ///
    /// ==================== 为什么用 SetFloat 的 dampTime 而不是自己插值 ====================
    /// README §2.2.2 要求「Idle ↔ Run 由速度参数驱动过渡，不硬切状态，避免抖动」。
    /// <c>Animator.SetFloat(id, value, dampTime, deltaTime)</c> 由引擎在参数层面做阻尼，
    /// 与 Animator Controller 里的过渡时长叠加，是官方推荐做法；自己维护一份插值状态
    /// 只会多一个可能与控制器参数打架的写入者。
    /// </summary>
    [DisallowMultipleComponent]
    public class AnimationComponent : MonoBehaviour
    {
        /// <summary>
        /// 速度参数的归一化基准为 0 时的兜底值（米/秒）。
        /// 用途：单位没有移动组件（建筑）或配置速度为 0 时，避免除零把参数算成 NaN
        /// （NaN 写进 Animator 参数后，所有以它为条件的过渡都会失效且不报错）。
        /// </summary>
        private const float MinSpeedReference = 0.01f;

        [Header("动画引用（由一键组装工具注入；留空则自动在子节点里找）")]
        [Tooltip("本单位的 Animator。白盒期挂在预制体根节点上；接入真实模型后通常挂在模型自身。\n" +
                 "留空时会在 Awake 里用 GetComponentInChildren 补一次解析，因此子节点上的 Animator 也能命中。")]
        [SerializeField] private Animator animator;

        [Header("预留：真实 3D 模型（阶段九资产导入用）")]
        [Tooltip("真实 3D 模型预制体。白盒期为 null（保持胶囊体 / 方块外观），\n" +
                 "导入模型后由一键组装工具把资产路径 Assets/Art/Models/** 下的模型注入到这里，\n" +
                 "再由工具把模型挂成预制体的子节点（本组件不参与模型挂载，只持有引用供工具与调试读取）。")]
        [SerializeField] private GameObject modelPrefab;

        [Header("总开关")]
        [Tooltip("关掉它等于「整块关闭动画」——逻辑、伤害与胜负结果完全不变（README §3.4 约束 4 的判据）。")]
        [SerializeField] private bool enableAnimation = true;

        [Header("Animator 参数名（与 UnitAnimator.controller 一一对应）")]
        [Tooltip("移动速度参数（float）。0 = 静止，1 = 以配置速度满速移动；由 Idle ↔ Run 的过渡条件读取。")]
        [SerializeField] private string speedParameterName = "Speed";

        [Tooltip("普攻触发参数（trigger）。由 CombatComponent.OnAttackPerformed 驱动。")]
        [SerializeField] private string attackTriggerName = "Attack";

        [Tooltip("施法触发参数（trigger）。由 SkillComponent.OnCastStarted 驱动（前摇开始即播施法动作）。")]
        [SerializeField] private string spellTriggerName = "Spell";

        [Tooltip("死亡触发参数（trigger）。由 HealthComponent.OnDied 驱动，只播一次。")]
        [SerializeField] private string dieTriggerName = "Die";

        [Header("速度参数")]
        [Tooltip("速度参数的阻尼时间（秒）。0.1 秒是 MOBA 惯用值：既不抖动，也不拖泥带水。")]
        [Min(0f)]
        [SerializeField] private float speedDampTime = 0.1f;

        [Tooltip("速度死区（归一化后）。低于该值一律按 0 处理，避免单位被避让推挤时\n" +
                 "在 0.02 这类噪声值上反复触发 Idle ↔ Run 过渡。")]
        [Range(0f, 0.5f)]
        [SerializeField] private float speedDeadZone = 0.05f;

        [Tooltip("速度归一化的分母（米/秒）。0 = 自动取 MovementComponent.Speed（配置移速）。\n" +
                 "只有「动画播放速率与逻辑移速刻意解耦」时才需要手填。")]
        [Min(0f)]
        [SerializeField] private float speedReference = 0f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次动画触发与死亡锁定（排查「动画不播」时打开）。")]
        [SerializeField] private bool logAnimationEvents = false;

        // ---- 延迟解析的组件引用（与项目其它表现层组件同一口径）----
        private MovementComponent movement;
        private CombatComponent combat;
        private HealthComponent health;
        private SkillComponent skills;

        // ---- 参数哈希（在 Awake 里按上面的字符串解析，因此改参数名不需要改代码）----
        private int speedParameterHash;
        private int attackTriggerHash;
        private int spellTriggerHash;
        private int dieTriggerHash;

        /// <summary>是否已订阅逻辑事件。用于保证订阅 / 退订严格配对。</summary>
        private bool hasSubscribed;

        /// <summary>是否已就"缺少 Animator"告警过一次（防刷屏）。</summary>
        private bool hasReportedMissingAnimator;

        /// <summary>是否已死亡。死亡后不再驱动速度、不再接受攻击 / 施法触发（尸体不许跑步）。</summary>
        private bool isDead;

        /// <summary>本组件当前是否具备可用的动画驱动能力（Animator 存在且开关打开）。只读，供调试与工具校验。</summary>
        public bool IsAnimationReady => enableAnimation && animator != null && animator.runtimeAnimatorController != null;

        /// <summary>当前 Animator 引用（只读），供调试视图与编辑器校验读取。</summary>
        public Animator Animator => animator;

        /// <summary>预留的真实模型预制体（只读）。白盒期为 null。</summary>
        public GameObject ModelPrefab => modelPrefab;

        /// <summary>是否已死亡（只读）。</summary>
        public bool IsDead => isDead;

        private void Awake()
        {
            // Animator 允许挂在子节点上（真实模型通常自带 Animator），因此用 InChildren 解析。
            // 必须在 Awake 里解析：AddComponent 会同步触发 Awake，工具在之后注入的显式引用会覆盖它，
            // 两种来源（自动解析 / 工具注入）最终都落在同一个字段上，没有第二个写入者。
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>(true);
            }

            ResolveParameterHashes();
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        /// <summary>
        /// 兜底补一次订阅。
        /// 必要性：用代码动态创建的单位（MinionSpawner 生成的小兵）在 Instantiate 的同一调用栈里
        /// 就会走完 Awake / OnEnable，而此时若某个组件是后挂的，OnEnable 里的解析会落空；
        /// Start 晚于整个 Instantiate 调用栈，是最后一次"引用必然已就位"的时机。
        /// TrySubscribe 是幂等的，因此这里重复调用没有副作用。
        /// </summary>
        private void Start()
        {
            TrySubscribe();
        }

        private void Update()
        {
            if (!enableAnimation || animator == null)
            {
                return;
            }

            // 死亡后速度参数锁定为 0：否则尸体在滑行 / 被推挤时会一直播跑步动画。
            if (isDead)
            {
                animator.SetFloat(speedParameterHash, 0f);
                return;
            }

            float normalizedSpeed = ResolveNormalizedSpeed();

            if (speedDampTime > 0f)
            {
                // 引擎侧阻尼：与 Animator Controller 里 0.1 秒的过渡叠加，得到"既不抖也不黏"的手感。
                animator.SetFloat(speedParameterHash, normalizedSpeed, speedDampTime, Time.deltaTime);
            }
            else
            {
                animator.SetFloat(speedParameterHash, normalizedSpeed);
            }
        }

        #region 事件订阅

        /// <summary>
        /// 订阅全部逻辑事件。幂等：重复调用只生效一次。
        /// 【顺序】先解析组件、再订阅；任一组件缺失都只跳过它自己那条通道 ——
        /// 小兵没有技能（SkillComponent 为 null）属于正常配置，不该因此丢掉它的受击 / 死亡动画。
        /// </summary>
        private void TrySubscribe()
        {
            if (hasSubscribed)
            {
                return;
            }

            ResolveComponents();

            if (health == null && combat == null && skills == null)
            {
                // 三个事件源一个都没有：说明组件还没挂齐（动态创建途中）。留给 Start 再试一次。
                return;
            }

            if (health != null)
            {
                health.OnDied += HandleDied;
            }

            if (combat != null)
            {
                combat.OnAttackPerformed += HandleAttackPerformed;
            }

            if (skills != null)
            {
                skills.OnCastStarted += HandleCastStarted;
            }

            hasSubscribed = true;
        }

        /// <summary>退订全部逻辑事件。与 TrySubscribe 严格配对，避免对象被禁用后仍收到回调。</summary>
        private void Unsubscribe()
        {
            if (!hasSubscribed)
            {
                return;
            }

            hasSubscribed = false;

            if (health != null)
            {
                health.OnDied -= HandleDied;
            }

            if (combat != null)
            {
                combat.OnAttackPerformed -= HandleAttackPerformed;
            }

            if (skills != null)
            {
                skills.OnCastStarted -= HandleCastStarted;
            }
        }

        /// <summary>解析宿主与各逻辑组件引用（允许为空，逐个判空处理）。</summary>
        private void ResolveComponents()
        {
            if (movement == null)
            {
                movement = GetComponent<MovementComponent>();
            }

            if (combat == null)
            {
                combat = GetComponent<CombatComponent>();
            }

            if (health == null)
            {
                health = GetComponent<HealthComponent>();
            }

            if (skills == null)
            {
                skills = GetComponent<SkillComponent>();
            }
        }

        #endregion

        #region 逻辑事件 → 动画触发

        /// <summary>
        /// 普攻出手 → 播攻击动作。
        /// 事件来源是 <c>CombatComponent.OnAttackPerformed</c>，它在"一次攻击被接受"的唯一收敛点广播
        /// （近战走 TryAttack → TryCommitAttack，塔走 TryCommitAttack → 发射弹道，两条路径共用）。
        /// 参数（目标）刻意不使用：动画只需要知道"出手了"，打谁不影响动作。
        /// </summary>
        private void HandleAttackPerformed(ITargetable target)
        {
            PlayAttack();
        }

        /// <summary>
        /// 施法开始（前摇进入）→ 播施法动作。
        ///
        /// 【为什么订阅 OnCastStarted 而不是 OnSpellReleased】
        ///  - README §3.4 的事件契约写明「OnCastStarted → 施法动画 + 施法特效（校验通过、扣蓝起 CD 之后）」；
        ///    OnSpellReleased 是"前摇结束、效果真正生效"的时刻，那时再播施法动作已经晚了。
        ///  - 拒绝路径（CD / 蓝不足 / 超距 / 目标非法）不会广播任何事件，因此"没放出去却播了施法动画"
        ///    在结构上不可能发生。
        ///  - 注意事件名：项目里**没有** OnSpellCast 这个事件，真实存在的是
        ///    OnCastStarted / OnSpellReleased 这一对（详见 SkillComponent 的声明）。
        /// </summary>
        private void HandleCastStarted(SkillSlot slot, SkillData data)
        {
            PlaySpell();
        }

        /// <summary>生命归零 → 播死亡动作（只播一次）。</summary>
        private void HandleDied()
        {
            PlayDeath();
        }

        #endregion

        #region 公开表现入口（供本组件的内部事件处理与外部纯表现调用）

        /// <summary>触发一次攻击动画。幂等语义：Animator 的 trigger 在一次消费后自动复位，重复触发不会叠加。</summary>
        public void PlayAttack()
        {
            if (!CanPlayAction())
            {
                return;
            }

            animator.SetTrigger(attackTriggerHash);

            if (logAnimationEvents)
            {
                Debug.Log($"[AnimationComponent] {name} 触发 Attack（{attackTriggerName}）。", this);
            }
        }

        /// <summary>触发一次施法动画。施法者已死亡时不播（尸体不施法）。</summary>
        public void PlaySpell()
        {
            if (!CanPlayAction())
            {
                return;
            }

            animator.SetTrigger(spellTriggerHash);

            if (logAnimationEvents)
            {
                Debug.Log($"[AnimationComponent] {name} 触发 Spell（{spellTriggerName}）。", this);
            }
        }

        /// <summary>
        /// 触发死亡动画。**只播一次**：<see cref="isDead"/> 一旦置位，后续任何触发都会被忽略
        /// （README §2.2.2 明确要求「Death 只播一次」，且 HealthComponent 本身也保证 OnDied 只广播一次，
        /// 这里是第二道保险 —— 表现层不该依赖上游"只发一次"的承诺来保证自己的正确性）。
        /// </summary>
        public void PlayDeath()
        {
            if (isDead)
            {
                return;
            }

            isDead = true;

            if (!enableAnimation || animator == null)
            {
                return;
            }

            // 先把速度归零再触发死亡：否则本帧 Update 可能已经把速度参数写成"奔跑"，
            // 而 Animator 的 AnyState → Die 过渡在同一帧被消费，观感上会有一次跑步的抽帧。
            animator.SetFloat(speedParameterHash, 0f);
            animator.SetTrigger(dieTriggerHash);

            if (logAnimationEvents)
            {
                Debug.Log($"[AnimationComponent] {name} 触发 Die（{dieTriggerName}），速度参数已锁定为 0。", this);
            }
        }

        #endregion

        #region 内部工具

        /// <summary>攻击 / 施法触发的统一前置条件：开关打开、Animator 就绪、且未死亡。</summary>
        private bool CanPlayAction()
        {
            if (isDead || !enableAnimation)
            {
                return false;
            }

            if (animator == null)
            {
                ReportMissingAnimatorOnce();
                return false;
            }

            if (animator.runtimeAnimatorController == null)
            {
                // 白盒期允许没有控制器（没有动画可播），但必须让使用者看得见 —— 否则"配了却没反应"无从排查。
                ReportMissingAnimatorOnce();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 计算归一化移动速度（0 ~ 1+）。
        /// 分子取 <c>CurrentVelocity</c>（本帧真实速度），分母取配置移速 —— 这样减速 buff
        /// 会自然地让动画慢下来，而眩晕 / 前摇 / 拥堵导致的静止会自然回到 Idle。
        /// </summary>
        private float ResolveNormalizedSpeed()
        {
            if (movement == null)
            {
                // 没有移动组件（防御塔 / 基地）：恒为静止。它们通常也没有 Animator，
                // 这里返回 0 而不是直接 return，是为了让"给建筑挂了动画"这种配置也能得到合理表现。
                return 0f;
            }

            float reference = ResolveSpeedReference();

            if (reference <= MinSpeedReference)
            {
                return 0f;
            }

            float normalized = movement.CurrentVelocity.magnitude / reference;

            // 死区：被邻居推挤时速度会在 0.01 ~ 0.05 之间抖，直接喂给 Animator 会让 Idle ↔ Run
            // 在阈值附近来回切（README 验收项明确要求「连续小范围移动 10 次不出现同帧来回切换」）。
            return normalized < speedDeadZone ? 0f : normalized;
        }

        /// <summary>取速度归一化的分母：手填值优先，为 0 时退回配置移速。</summary>
        private float ResolveSpeedReference()
        {
            if (speedReference > MinSpeedReference)
            {
                return speedReference;
            }

            // MovementComponent.Speed 是 NavMeshAgent.speed（配置移速），不是本帧速度 —— 这正是分母该有的语义。
            return movement != null ? movement.Speed : 0f;
        }

        /// <summary>
        /// 按字段里配置的参数名解析哈希。
        /// 放在 Awake（而不是静态字段初始化器）里：字段值来自序列化数据，且
        /// "MonoBehaviour 的字段初始化器里不做任何引擎相关调用"是项目踩过坑的硬约定。
        /// </summary>
        private void ResolveParameterHashes()
        {
            speedParameterHash = Animator.StringToHash(speedParameterName);
            attackTriggerHash = Animator.StringToHash(attackTriggerName);
            spellTriggerHash = Animator.StringToHash(spellTriggerName);
            dieTriggerHash = Animator.StringToHash(dieTriggerName);
        }

        /// <summary>
        /// 就"没有可用的 Animator / 控制器"告警一次。
        ///
        /// 【为什么是 Warning 而不是 LogError + enabled = false】
        /// README §6.2 的硬性要求针对的是"缺失即功能不可用"的必需依赖（如 EntityBase、NavMeshAgent）。
        /// 动画是**可选的表现能力**：白盒期不给单位挂 Animator 是完全合法的配置，
        /// 单位仍然能移动、攻击、被击杀，对局结果一字不差。
        /// 因此这里只提示一次（并给出补法），不把组件禁用 —— 禁用会让"事后补上 Animator"
        /// 无法自动恢复，反而制造一个更难排查的静默状态。
        /// </summary>
        private void ReportMissingAnimatorOnce()
        {
            if (hasReportedMissingAnimator)
            {
                return;
            }

            hasReportedMissingAnimator = true;

            Debug.LogWarning(
                $"[AnimationComponent] {name} 没有可用的 Animator 或 AnimatorController，动画不会播放" +
                "（不影响任何对局逻辑）。\n" +
                "  · 修法：执行「MOBA Demo/一键组装测试战场」，工具会生成 Assets/Art/Animations/UnitAnimator.controller " +
                "并把它连同 Animator 组件注入英雄 / 小兵预制体。\n" +
                "  · 若只是想临时关掉动画：把本组件的 enableAnimation 取消勾选即可，不必删组件。", this);
        }

        /// <summary>
        /// 编辑器内校验：参数名为空会在运行期得到 hash 0，而 0 恰好是"无效参数"，
        /// SetFloat / SetTrigger 会静默失败（Unity 只在 Development Build 里才可能报警）。
        /// 因此把"参数名不能为空"这条约束提前到编辑期。
        /// </summary>
        private void OnValidate()
        {
            speedParameterName = NormalizeParameterName(speedParameterName, "Speed");
            attackTriggerName = NormalizeParameterName(attackTriggerName, "Attack");
            spellTriggerName = NormalizeParameterName(spellTriggerName, "Spell");
            dieTriggerName = NormalizeParameterName(dieTriggerName, "Die");

            speedDampTime = Mathf.Max(0f, speedDampTime);
            speedReference = Mathf.Max(0f, speedReference);
        }

        /// <summary>把空的参数名回填成默认值（空字符串会让 Animator 参数查找静默失败）。</summary>
        private static string NormalizeParameterName(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        #endregion
    }
}

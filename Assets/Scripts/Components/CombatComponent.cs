using System;
using UnityEngine;
using MOBA.Core;
using MOBA.Data;
using MOBA.Skills;

namespace MOBA.Components
{
    /// <summary>
    /// 战斗组件：负责"能不能打"和"打出这一下伤害"，不负责"该不该打"。
    ///
    /// 职责边界（对应计划书 3.2）：
    /// 1. 提供攻击距离判定（CanAttack）与一次攻击结算（TryAttack）；
    /// 2. 不搜索目标（那是 TargetingComponent 的事），不驱动移动（那是 FSM 与 MovementComponent 的事），
    ///    也不决定攻击节奏的调度时机——由 FSM 的 AttackState 或玩家指令在合适的时候调用 TryAttack；
    /// 3. 只依赖 ITargetable / IDamageable 接口，因此不关心目标是英雄、小兵还是防御塔。
    ///
    /// 运行时状态（nextAttackTime）保存在本组件实例中，绝不写回 AttackData 资产。
    /// </summary>
    [DisallowMultipleComponent]
    public class CombatComponent : MonoBehaviour
    {
        [Header("攻击配置")]
        [Tooltip("普通攻击参数。优先由 EntityBase 从 EntityStatsData.Attack 注入；" +
                 "若配置资产里没填，则沿用这里在 Inspector 上直接指定的引用。")]
        [SerializeField] private AttackData attackData;

        [Header("调试")]
        [Tooltip("在 Console 输出每次成功攻击的伤害信息。")]
        [SerializeField] private bool logAttackEvents = true;

        [Tooltip("选中该对象时，在 Scene 视图中绘制攻击范围。")]
        [SerializeField] private bool drawAttackRangeGizmos = true;

        [Header("运行时状态（只读，仅供 Inspector 观察调试）")]
        [Tooltip("攻击锁。为 true 时一切攻击尝试都被拒绝，由 BuffComponent（眩晕）与 SkillComponent（施法前摇）控制。")]
        [SerializeField] private bool attackLocked = false;

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

        /// <summary>索敌组件（可能为 null）。有它时直接复用它内部的敌我规则，避免两处判断不一致。</summary>
        private TargetingComponent targeting;

        /// <summary>
        /// 本单位的 BuffComponent（延迟解析，可能为 null）。
        /// 用途只有一个：取走「强化下一次普攻」（阶段八 Q 致命打击）。
        /// 允许为 null —— 没有状态容器的单位（例如被误配的建筑）自然也就不会有强化状态。
        /// </summary>
        private BuffComponent selfBuff;

        /// <summary>是否已就"缺少 EntityBase"报过错误，防止每次攻击判定都刷日志。</summary>
        private bool hasReportedMissingOwner;

        /// <summary>是否已就"缺少 AttackData"报过错误。</summary>
        private bool hasReportedMissingAttackData;

        /// <summary>
        /// 下次允许攻击的时间点，基准是 Time.time（秒）。
        /// 用"绝对时间点"而不是"剩余冷却秒数"来存：后者需要每帧递减，一旦组件被禁用就会漏减；
        /// 前者只需在攻击时写一次，天然不受帧率与启停影响。
        /// </summary>
        private float nextAttackTime;

        /// <summary>当前使用的攻击配置（只读），供调试视图与 UI 读取。</summary>
        public AttackData AttackData => attackData;

        /// <summary>攻击距离（只读）；未配置攻击数据时返回 0，表示无法攻击。</summary>
        public float AttackRange => attackData != null ? attackData.AttackRange : 0f;

        /// <summary>攻击冷却是否已就绪（只读），供 UI 显示技能可用状态。</summary>
        public bool IsCooldownReady => Time.time >= nextAttackTime;

        /// <summary>距离下次可攻击还剩多少秒；已就绪返回 0。供技能图标转圈等 UI 使用。</summary>
        public float CooldownRemaining => Mathf.Max(0f, nextAttackTime - Time.time);

        /// <summary>当前是否处于攻击锁状态（只读），供调试与测试断言使用。</summary>
        public bool IsAttackLocked => attackLocked;

        /// <summary>
        /// 攻击出手事件（阶段九新增）：一次攻击已通过全部前置校验、并已消耗攻击冷却时广播。
        /// 参数是被攻击的目标（供表现层做朝向 / 目标相关的表现；不需要时忽略即可）。
        ///
        /// 【语义边界 —— 它报告的是「打出去了」，不是「打中了」】
        ///  · 近战单位（英雄 / 小兵）：TryAttack → TryCommitAttack（广播）→ 同帧结算伤害。
        ///  · 弹道单位（防御塔）：TowerController → TryCommitAttack（广播）→ 发射弹道 → 命中时才结算。
        /// 因此"命中反馈"必须由 <see cref="HealthComponent.OnDamaged"/> 承担，
        /// 拿本事件当命中信号会让塔的受击表现在弹道飞出的瞬间就炸开。
        ///
        /// 【为什么现在才造这个事件（阶段八时被明确搁置）】
        /// README §3.4 有一条硬约定：本项目**不留没有消费方的僵尸事件**。阶段八讨论受击反馈时
        /// 就明确写过「普攻目前没有独立的『已出手』事件……留待阶段九由 AnimationComponent 接管」。
        /// 阶段九的 <c>AnimationComponent</c>（Attack 触发）与 <c>VfxSpawner</c>（出手特效）
        /// 正是它的消费方，因此现在补齐 —— 事件名与 README §3.4 表格里的约定一致。
        ///
        /// 【为什么在 TryCommitAttack 里广播，而不是在 TryAttack 里】
        /// TryCommitAttack 是"一次攻击被接受"的**唯一收敛点**：近战与塔两条路径都经过它
        /// （塔刻意不自己写一份冷却，见 TryCommitAttack 的说明）。放在那里，两条路径的
        /// 动画 / 特效触发天然一致，也不会出现"塔发射弹道时没有出手表现"的缺口。
        /// </summary>
        public event Action<ITargetable> OnAttackPerformed;

        /// <summary>
        /// 设置攻击锁（阶段六新增）。
        ///
        /// 【为什么需要它】与 MovementComponent.SetMovementLocked 完全对称：眩晕与施法前摇都要求"这段时间内不许出手"。
        /// 若不加闸门，就得在每个调用 TryAttack 的地方（玩家指令层 + FSM 的 AttackState）各判一次状态，
        /// 而攻击比移动更敏感——一次漏判就表现为"被眩晕的单位还在平A"，是肉眼可见的破绽。
        ///
        /// 放在 CanAttack 内部而不是 TryAttack 的入口，是为了让"能否攻击"这个问题的答案只有一处，
        /// 避免调用方绕过锁直接结算伤害（TryAttack 的第一步就是 CanAttack，因此自动被覆盖）。
        /// </summary>
        /// <param name="locked">true = 禁止攻击；false = 恢复。</param>
        public void SetAttackLocked(bool locked)
        {
            attackLocked = locked;
        }

        /// <summary>
        /// 缓存索敌组件引用（可选依赖，允许为 null）。
        /// EntityBase 引用不在这里校验，改由 <see cref="Owner"/> 在首次使用时延迟解析并报错——
        /// 这样既不会对"动态创建、EntityBase 后挂"的合法用法误报，也不会漏掉真正的缺失。
        /// </summary>
        private void Awake()
        {
            targeting = GetComponent<TargetingComponent>();
        }

        /// <summary>
        /// 注入攻击配置。由 EntityBase 在 Start 阶段用 EntityStatsData.Attack 调用。
        /// 约定：传入 null 时保留 Inspector 上已有的引用（而不是把它清空），
        /// 这样"配置资产里没填 Attack"的旧预制体仍然可以靠 Inspector 直挂的方式正常工作。
        /// </summary>
        /// <param name="data">攻击配置资产，允许为 null。</param>
        public void Initialize(AttackData data)
        {
            if (data == null)
            {
                if (attackData == null)
                {
                    Debug.LogWarning(
                        $"[CombatComponent] {name} 未获得有效的 AttackData（配置资产与 Inspector 均为空），该单位无法攻击。", this);
                }
                return;
            }

            attackData = data;
        }

        /// <summary>
        /// 判断现在能否对指定目标发起攻击。四个条件全部满足才返回 true：
        /// 1. 攻击配置存在；2. 目标是存活且可被选中的敌方单位；3. 目标在攻击距离内；4. 攻击冷却已就绪。
        /// </summary>
        /// <param name="target">待攻击的目标，允许为 null。</param>
        /// <returns>可以攻击返回 true。</returns>
        public bool CanAttack(ITargetable target)
        {
            // 攻击锁闸门放在最前面：被眩晕或正在施法前摇时，任何目标都不该能打出去。
            // 返回 false 且不打日志——这是"当前不允许攻击"的正常语义，不是配置错误；
            // 若在此告警，一次 1 秒的眩晕会刷出几十条无意义日志，把真正的配置问题淹没。
            if (attackLocked)
            {
                return false;
            }

            if (attackData == null)
            {
                if (!hasReportedMissingAttackData)
                {
                    hasReportedMissingAttackData = true;
                    Debug.LogError($"[CombatComponent] {name} 没有 AttackData，无法攻击。请检查配置。", this);
                }
                return false;
            }

            if (Owner == null)
            {
                if (!hasReportedMissingOwner)
                {
                    hasReportedMissingOwner = true;
                    Debug.LogError(
                        $"[CombatComponent] {name} 上找不到 EntityBase，无法判断敌我，攻击功能不可用。" +
                        "请确保 EntityBase 与 CombatComponent 挂在同一个 GameObject 上。", this);
                }
                return false;
            }

            // 空引用保护：调用方（FSM / 玩家指令）很可能直接传入 TargetingComponent.CurrentTarget，
            // 而那个值完全可能是 null，因此这里必须判空而不是依赖调用方。
            if (target == null)
            {
                return false;
            }

            // 目标 GameObject 可能已被销毁（接口引用不会自动变 null），必须先做存活检查再访问其属性。
            if (!IsAlive(target))
            {
                return false;
            }

            // 已死亡 / 不可选中的目标不能打——这条也是"死亡后立即停止攻击"的关键闸门。
            if (!target.IsValidTarget)
            {
                return false;
            }

            // 不能攻击友方：即使调用方绕过了 TargetingComponent 直接传参，这里也会拦住。
            if (!IsEnemy(target))
            {
                return false;
            }

            if (!IsInAttackRange(target))
            {
                return false;
            }

            return IsCooldownReady;
        }

        /// <summary>
        /// 尝试攻击目标：满足 CanAttack 时结算一次伤害并重置冷却。
        /// </summary>
        /// <param name="target">攻击目标。</param>
        /// <returns>本次攻击是否真的打出去了（返回 false 表示条件不满足，未造成任何伤害）。</returns>
        public bool TryAttack(ITargetable target)
        {
            // 前置校验与冷却消耗统一交给 TryCommitAttack（"什么算一次攻击"只有一份实现）。
            if (!TryCommitAttack(target))
            {
                return false;
            }

            // 此处再解析一次承伤方：TryCommitAttack 内已经解析并校验过一次，但它不把结果带出来
            // （带出来就要引入 out 参数，而绝大多数调用方并不需要）。GetComponentInParent 是
            // 缓存友好的组件查找，一次额外调用远低于"两套冷却写入路径"的维护代价。
            IDamageable damageable = ResolveDamageable(target);

            // ---- 阶段八：取走「强化下一次普攻」 ----
            // 【为什么放在 TryCommitAttack 之后】那一步已经通过了全部前置校验（含"目标真的可承伤"），
            // 因此走到这里就一定会结算一次伤害 —— 这正是"命中即消耗"的语义。
            // 若放在校验之前，一次"打不出去"的攻击会白白吃掉一层强化。
            float damage = attackData.Damage;
            float silenceDuration = 0f;

            if (selfBuff == null)
            {
                selfBuff = GetComponent<BuffComponent>();
            }

            if (selfBuff != null && selfBuff.TryConsumeEmpowerNextAttack(out float bonusDamage, out float silence))
            {
                damage += bonusDamage;
                silenceDuration = silence;
            }

            if (logAttackEvents)
            {
                // 强化生效时把额外伤害一并打出来：否则排查"强化到底有没有生效"只能靠对比两次伤害数字。
                string empowerText = damage > attackData.Damage
                    ? $"（含强化普攻 +{damage - attackData.Damage:F1}）"
                    : string.Empty;

                Debug.Log(
                    $"[CombatComponent] {name} 攻击 {DescribeTarget(target)}，造成 {damage:F1} 点伤害{empowerText}" +
                    $"（距离 {Vector3.Distance(transform.position, target.TargetTransform.position):F2} / 射程 {attackData.AttackRange:F2}）。", this);
            }

            // 真正的伤害入口：本组件不关心对方是英雄还是建筑，也不参与扣血细节。
            //
            // 【必须带上伤害归属】用单参数重载会让 HealthComponent.LastDamageSource 变成 null，
            // 后果有两条，而且都不会报任何错：
            //   ① 击杀播报把"被普攻打死"记成"无归属击杀"（MatchStatsTracker 读的就是这个字段）；
            //   ② 阶段八的防御塔仇恨转移依赖"伤害来源是哪个敌方英雄"（规则 2：英雄抗塔），
            //      来源恒为 null 时转移永远不会触发，表现为"塔对英雄的挑衅毫无反应"。
            // 归属方就是本组件的宿主（Owner）——伤害是谁打出来的，只有本组件知道。
            damageable.TakeDamage(damage, Owner);

            // ---- 强化普攻附带的沉默 ----
            // 在伤害之后结算：目标可能被这一下打死，而"对尸体施加沉默"是没有意义的
            // （BuffComponent 会因目标已死亡而被 SkillEffectResolver 拒绝，但先判一下能省掉一次组件查找）。
            if (silenceDuration > 0f)
            {
                SkillEffectResolver.ApplySilence(target, silenceDuration);
            }

            return true;
        }

        /// <summary>
        /// 提交一次「延迟结算」的攻击（阶段八新增，供防御塔的弹道普攻使用）：
        /// 通过全部前置校验并【消耗一次攻击冷却】，但【不立即结算伤害】——
        /// 伤害由调用方在弹道命中时经 SkillEffectResolver.ApplyDamage 结算。
        ///
        /// 【为什么必须有这个入口】弹道普攻的"打出这一下"与"打中"被时间拆开了。
        /// 若塔自己写一份 nextAttackTime，攻击节奏就有两个写入方（本类与塔），
        /// 迟早出现"塔的攻速与 AttackData 配置对不上"这类只有实测才发现的问题。
        /// 把冷却的唯一写入点留在本类，塔只负责"按节奏发弹道"。
        ///
        /// 【与 TryAttack 的关系】TryAttack = TryCommitAttack + 立即结算。
        /// 两者共用同一套校验（含"目标必须真的可承伤"这一步），
        /// 因此"打不出去的那一次不会白吃一个攻击间隔"这条既有性质对两者都成立。
        /// </summary>
        /// <param name="target">攻击目标。</param>
        /// <returns>本次攻击已被接受（冷却已消耗）返回 true；条件不满足返回 false 且无任何副作用。</returns>
        public bool TryCommitAttack(ITargetable target)
        {
            if (!CanAttack(target))
            {
                return false;
            }

            // 【顺序很关键】解析承伤方 → 二次校验 → 重置冷却，三步都不能调换：
            //
            // 1. 先解析 IDamageable 并做"鞭尸"二次校验，且必须在写冷却【之前】返回。
            //    原因：CanAttack 判断的是 ITargetable 实现方的 IsValidTarget，而真正承伤的可能是另一个组件
            //    （本项目 ITargetable 由 EntityBase 实现、IDamageable 由 HealthComponent 实现）。
            //    若把冷却写在前面，一次"打不出去"的调用会白吃一个攻击间隔，
            //    验收时表现为"实际攻击间隔比 AttackData 配置值长"。
            //
            // 2. 冷却必须在伤害结算之前写入：TakeDamage 会同步触发 OnDied，
            //    监听方（控制器 / UI）可能在回调里立刻再次尝试攻击（例如"目标死了就换下一个打"），
            //    若冷却写在伤害之后，那次重入会看到"冷却已就绪"而在同一帧再打一发。
            IDamageable damageable = ResolveDamageable(target);
            if (damageable == null)
            {
                // 目标实现了 ITargetable 却没有可受伤组件（例如场景里的纯装饰物），属于配置遗漏。
                Debug.LogWarning($"[CombatComponent] {name} 的目标没有实现 IDamageable，本次伤害未结算。", this);
                return false;
            }

            if (damageable.IsDead)
            {
                return false;
            }

            nextAttackTime = Time.time + attackData.AttackInterval;

            // 广播出手（阶段九）：冷却已写入之后才广播，理由与上面那条顺序说明同源 ——
            // 订阅方（表现层）可能在回调里同步做别的事，此时"这一下已经打出去了"这个状态必须已经成立。
            OnAttackPerformed?.Invoke(target);

            return true;
        }

        /// <summary>
        /// 判断目标是否在攻击距离内。
        /// 当前使用"中心点距离"，适用于体型相近的单位；若后续加入体型差异很大的单位（如基地），
        /// 应改为按碰撞体/包围盒边缘计算距离，否则会出现"看起来挨着却打不到"的问题。
        /// </summary>
        private bool IsInAttackRange(ITargetable target)
        {
            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                return false;
            }

            // 用平方距离比较，省掉一次开方；两种写法等价，仅性能差异。
            float sqrDistance = (targetTransform.position - transform.position).sqrMagnitude;
            return sqrDistance <= attackData.AttackRange * attackData.AttackRange;
        }

        /// <summary>
        /// 敌我校验。优先复用 TargetingComponent 的规则，保证"能锁定"和"能攻击"用的是同一套判断；
        /// 没有索敌组件时退化为直接比较阵营，但【中立阵营的排除必须保持一致】——
        /// 否则会出现"索敌认为不是敌人、攻击却打得出去"的规则分叉（README 2.2：不可攻击中立单位）。
        /// </summary>
        private bool IsEnemy(ITargetable target)
        {
            if (targeting != null)
            {
                return targeting.IsEnemy(target);
            }

            // 退化路径：无索敌组件时自行判断，规则与 TargetingComponent.IsEnemy 保持逐条对齐。
            if (target.Team == TeamType.Neutral)
            {
                return false;
            }

            EntityBase resolvedOwner = Owner;
            return resolvedOwner != null && target.Team != resolvedOwner.Team;
        }

        /// <summary>
        /// 从目标身上解析出 IDamageable 实现。
        /// 本项目里 IDamageable 由 HealthComponent 实现，而 ITargetable 由 EntityBase 实现，
        /// 两者在同一个 GameObject 上但类型不同，因此不能简单地用 "target as IDamageable"，
        /// 需要通过组件查找把两者接起来。
        /// </summary>
        private static IDamageable ResolveDamageable(ITargetable target)
        {
            // 情况一：目标自己就实现了 IDamageable（例如单元测试里的桩对象）。
            if (target is IDamageable directDamageable)
            {
                return directDamageable;
            }

            // 情况二：目标是一个 Unity 组件（EntityBase），到它所在的物体及其父级上找 IDamageable 实现。
            if (target is Component targetComponent)
            {
                return targetComponent.GetComponentInParent<IDamageable>();
            }

            return null;
        }

        /// <summary>
        /// 判断 ITargetable 引用背后的 Unity 对象是否仍然存活。
        /// 接口引用在 GameObject 被销毁后不会变成 null，直接访问属性会抛 MissingReferenceException。
        /// </summary>
        private static bool IsAlive(ITargetable target)
        {
            if (target is UnityEngine.Object unityObject)
            {
                return unityObject != null;
            }

            return true;
        }

        /// <summary>生成目标的简短描述，仅用于日志。</summary>
        private static string DescribeTarget(ITargetable target)
        {
            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }

        /// <summary>
        /// Scene 视图可视化：绘制攻击范围。
        /// 与索敌范围对比可以直观发现"看得见却打不着"（攻击距离 &lt; 索敌距离是正常设计，
        /// 但若攻击距离大于索敌距离，AI 会永远追不上目标）。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!drawAttackRangeGizmos || attackData == null)
            {
                return;
            }

            // 橙色线框球 = 攻击距离。
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, attackData.AttackRange);

            // 冷却就绪时额外画一个实心小点，便于在 Scene 视图直接看出"现在能不能打"。
            if (Application.isPlaying && IsCooldownReady)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(transform.position + Vector3.up * 0.2f, 0.12f);
            }
        }
    }
}

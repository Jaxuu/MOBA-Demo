using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Skills
{
    /// <summary>
    /// 技能效果结算器（纯逻辑，静态无状态）。
    ///
    /// 【为什么必须有这样一个类】技能伤害有两条落地路径：弹道命中（Projectile）与范围场结算（AreaEffectZone）。
    /// 如果两处各自写一遍"解析 IDamageable → 判死亡 → 扣血"，规则迟早分叉——
    /// 最典型的后果是"弹道扣血走护盾、AOE 扣血绕过护盾"，而这种 bug 只在同时有护盾时才能被发现。
    /// 因此本项目规定：技能造成的一切效果【只经本类一个入口】。
    ///
    /// 职责边界：
    /// 1. 只做「把 SkillData 里配置的效果作用到一个目标上」，不选目标、不算距离、不碰冷却与蓝耗（那是 SkillComponent 的事）；
    /// 2. 不引用任何表现层类型（无 Animator / ParticleSystem / AudioSource / UI）；
    /// 3. 所有方法都返回 bool 表示"是否真的产生了效果"，调用方据此决定是否广播表现事件
    ///    —— 这样"没打中却播了命中特效"这类问题在结构上就不会发生。
    ///
    /// 【与 CombatComponent 的关系】普攻的伤害路径仍是 CombatComponent.TryAttack（它带距离与攻击间隔校验），
    /// 本类只服务技能。两者最终都落到 HealthComponent.TakeDamage，因此护盾/死亡的规则只有一份。
    /// </summary>
    public static class SkillEffectResolver
    {
        /// <summary>
        /// 按 SkillData 的主效果类型，把效果作用到目标身上。
        /// </summary>
        /// <param name="source">施法者（用于伤害归属与日志），允许为 null（则不会记录来源）。</param>
        /// <param name="target">效果承受方，必须是一个当前合法的目标。</param>
        /// <param name="data">技能配置。</param>
        /// <returns>是否真的产生了效果（false 表示目标非法、缺少必需组件、或数值为 0）。</returns>
        public static bool Apply(EntityBase source, ITargetable target, SkillData data)
        {
            if (data == null)
            {
                return false;
            }

            if (!IsUsable(target))
            {
                return false;
            }

            bool applied = ApplyPrimary(source, target, data);

            // 附带效果（V1 上限一个，见 SkillData.SecondaryEffectType）。
            // 刻意与主效果放在同一个入口里依次结算：调用点（Projectile / AreaEffectZone / SkillComponent）
            // 只调一次 Apply，不需要知道"这个技能有几个效果"，因此日后把附带效果升级成数组时，
            // 改动范围仍然只在本文件内。
            if (data.HasSecondaryEffect && ApplySecondary(source, target, data))
            {
                applied = true;
            }

            return applied;
        }

        /// <summary>
        /// 结算主效果（<see cref="SkillData.EffectType"/>）。
        /// </summary>
        /// <param name="source">施法者，允许为 null。</param>
        /// <param name="target">效果承受方。</param>
        /// <param name="data">技能配置。</param>
        /// <returns>是否真的产生了效果。</returns>
        private static bool ApplyPrimary(EntityBase source, ITargetable target, SkillData data)
        {
            switch (data.EffectType)
            {
                case SkillEffectType.Damage:
                    return ApplyDamage(target, data.Damage, source);

                case SkillEffectType.Shield:
                    return ApplyShield(target, data.ShieldValue, data.ShieldDuration);

                case SkillEffectType.Slow:
                    return ApplySlow(target, data.SlowPercent, data.SlowDuration);

                case SkillEffectType.Stun:
                    return ApplyStun(target, data.StunDuration);

                case SkillEffectType.EmpowerNextAttack:
                    return ApplyEmpowerNextAttack(
                        target, data.EmpowerBonusDamage, data.SilenceDuration, data.EmpowerDuration);

                case SkillEffectType.ExecuteDamage:
                    return ApplyExecuteDamage(target, data.Damage, data.ExecuteHealthRatio, source);

                case SkillEffectType.Heal:
                    return ApplyHeal(target, data.HealAmount);

                case SkillEffectType.Haste:
                    return ApplyHaste(target, data.HastePercent, data.HasteDuration);

                case SkillEffectType.None:
                    // 显式空效果：不是错误（附带效果槽位为 None 时会走到这里），静默返回。
                    return false;

                default:
                    Debug.LogWarning(
                        $"[SkillEffectResolver] 未处理的效果类型 {data.EffectType}（技能 {data.name}），本次效果未结算。");
                    return false;
            }
        }

        /// <summary>
        /// 结算附带效果。数值取自 <see cref="SkillData.SecondaryValue"/> / <see cref="SkillData.SecondaryDuration"/>，
        /// 含义按类型解释（Haste → 加速比例与时长；Heal → 治疗量；Shield → 护盾值与时长……）。
        /// </summary>
        /// <param name="source">施法者，允许为 null。</param>
        /// <param name="target">效果承受方。</param>
        /// <param name="data">技能配置。</param>
        /// <returns>是否真的产生了效果。</returns>
        private static bool ApplySecondary(EntityBase source, ITargetable target, SkillData data)
        {
            float value = data.SecondaryValue;
            float duration = data.SecondaryDuration;

            switch (data.SecondaryEffectType)
            {
                case SkillEffectType.Haste:
                    return ApplyHaste(target, value, duration);

                case SkillEffectType.Heal:
                    return ApplyHeal(target, value);

                case SkillEffectType.Shield:
                    return ApplyShield(target, value, duration);

                case SkillEffectType.Slow:
                    return ApplySlow(target, value, duration);

                case SkillEffectType.Stun:
                    return ApplyStun(target, duration);

                case SkillEffectType.Damage:
                    return ApplyDamage(target, value, source);

                case SkillEffectType.ExecuteDamage:
                    return ApplyExecuteDamage(target, value, data.ExecuteHealthRatio, source);

                case SkillEffectType.EmpowerNextAttack:
                    return ApplyEmpowerNextAttack(target, value, data.SilenceDuration, duration);

                default:
                    Debug.LogWarning(
                        $"[SkillEffectResolver] 未处理的附带效果类型 {data.SecondaryEffectType}" +
                        $"（技能 {data.name}），本次附带效果未结算。");
                    return false;
            }
        }

        /// <summary>
        /// 结算伤害。护盾的吸收逻辑在 HealthComponent 内部（护盾是伤害管线的一部分，必须与扣血同处）。
        /// </summary>
        /// <param name="target">承伤方。</param>
        /// <param name="damage">伤害值，非正数直接忽略。</param>
        /// <param name="source">伤害来源，允许为 null（阶段七的击杀归属会读它）。</param>
        /// <returns>是否真的扣了血（或扣了盾）。</returns>
        public static bool ApplyDamage(ITargetable target, float damage, EntityBase source)
        {
            if (damage <= 0f)
            {
                return false;
            }

            HealthComponent health = ResolveHealth(target);
            if (health == null || health.IsDead)
            {
                return false;
            }

            health.TakeDamage(damage, source);
            return true;
        }

        /// <summary>
        /// 结算斩杀伤害（阶段八新增）：伤害 = 基础值 + 系数 × 目标【已损失生命值】。
        ///
        /// 【为什么公式放在这里而不是让策划在资产里填"最终伤害"】斩杀的手感来源正是
        /// "目标越残越疼"——这个非线性只能由公式表达。基础值与系数都在 SkillData 上，
        /// 因此调整手感仍然是改资产，不需要改代码。
        /// </summary>
        /// <param name="target">承伤方。</param>
        /// <param name="baseDamage">基础伤害，非正数时仍可能因斩杀加成而生效。</param>
        /// <param name="lostHealthRatio">斩杀系数（0.35 = 目标已损失生命值的 35% 转化为额外伤害）。</param>
        /// <param name="source">伤害来源，允许为 null。</param>
        /// <returns>是否真的扣了血（或扣了盾）。</returns>
        public static bool ApplyExecuteDamage(
            ITargetable target, float baseDamage, float lostHealthRatio, EntityBase source)
        {
            HealthComponent health = ResolveHealth(target);
            if (health == null || health.IsDead)
            {
                return false;
            }

            float lostHealth = Mathf.Max(0f, health.MaxHealth - health.CurrentHealth);
            float damage = Mathf.Max(0f, baseDamage) + Mathf.Max(0f, lostHealthRatio) * lostHealth;

            if (damage <= 0f)
            {
                return false;
            }

            health.TakeDamage(damage, source);
            return true;
        }

        /// <summary>
        /// 结算治疗（阶段八新增）：本方法是 HealthComponent.Heal 的第一个消费方。
        /// 治疗量被 HealthComponent 内部夹到上限之内（血量不会超过最大值），
        /// 且死亡目标不生效（死亡不可逆，见 HealthComponent.Heal 的闸门）。
        /// </summary>
        /// <param name="target">受疗方。</param>
        /// <param name="amount">治疗量，非正数直接忽略。</param>
        /// <returns>是否真的产生了治疗（目标满血时返回 false，与"没打中"在语义上一致）。</returns>
        public static bool ApplyHeal(ITargetable target, float amount)
        {
            if (amount <= 0f)
            {
                return false;
            }

            HealthComponent health = ResolveHealth(target);
            if (health == null || health.IsDead)
            {
                return false;
            }

            // 满血时 Heal 内部会直接返回（不广播事件），这里提前判一次，
            // 让"是否真的产生了效果"这个返回值与"是否有数值变化"严格一致 ——
            // 否则表现层会为一个零治疗量的技能播一次治疗特效。
            if (health.CurrentHealth >= health.MaxHealth)
            {
                return false;
            }

            health.Heal(amount);
            return true;
        }

        /// <summary>
        /// 施加加速（阶段八新增）：移速的实际写入只经 MovementComponent.SetMoveSpeed。
        /// </summary>
        /// <param name="target">被加速方。</param>
        /// <param name="percent">加速比例（0.3 = 移速 130%）。</param>
        /// <param name="duration">持续时长（秒），非正数直接忽略。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplyHaste(ITargetable target, float percent, float duration)
        {
            if (percent <= 0f || duration <= 0f)
            {
                return false;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                LogMissingComponent(target, nameof(BuffComponent), "加速");
                return false;
            }

            buff.ApplyHaste(percent, duration);
            return true;
        }

        /// <summary>
        /// 施加沉默（阶段八新增）：禁止施法，不影响移动与普攻。
        /// 拦截点在 SkillComponent.TryCast 的自身状态校验链上，本方法只负责把状态挂上去。
        /// </summary>
        /// <param name="target">被沉默方。</param>
        /// <param name="duration">持续时长（秒），非正数直接忽略。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplySilence(ITargetable target, float duration)
        {
            if (duration <= 0f)
            {
                return false;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                LogMissingComponent(target, nameof(BuffComponent), "沉默");
                return false;
            }

            buff.ApplySilence(duration);
            return true;
        }

        /// <summary>
        /// 挂上「强化下一次普攻」（阶段八新增）：由 CombatComponent 在下一次普攻命中时取走。
        /// </summary>
        /// <param name="target">获得强化的单位（Self 施法时就是施法者自己）。</param>
        /// <param name="bonusDamage">额外伤害，非正数直接忽略。</param>
        /// <param name="silenceDuration">命中后对目标施加的沉默时长（秒）；0 = 不附带沉默。</param>
        /// <param name="duration">兜底超时（秒），非正数直接忽略。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplyEmpowerNextAttack(
            ITargetable target, float bonusDamage, float silenceDuration, float duration)
        {
            if (bonusDamage <= 0f || duration <= 0f)
            {
                return false;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                LogMissingComponent(target, nameof(BuffComponent), "强化普攻");
                return false;
            }

            buff.ApplyEmpowerNextAttack(bonusDamage, silenceDuration, duration);
            return true;
        }

        /// <summary>
        /// 施加护盾。护盾【数值】由 HealthComponent 持有（唯一能"优先吸收伤害"的地方），
        /// 【时长】由 BuffComponent 持有（到期时回调 ClearShield）——两边各管一件事，互不越界。
        /// </summary>
        /// <param name="target">受盾方。</param>
        /// <param name="value">护盾值，非正数直接忽略。</param>
        /// <param name="duration">持续时长（秒）；填 0 表示永久护盾，直接写入 HealthComponent 不设到期。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplyShield(ITargetable target, float value, float duration)
        {
            if (value <= 0f)
            {
                return false;
            }

            HealthComponent health = ResolveHealth(target);
            if (health == null || health.IsDead)
            {
                return false;
            }

            if (duration <= 0f)
            {
                // 永久护盾：没有"到期"这回事，交给 HealthComponent 即可。
                health.AddShield(value);
                return true;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                LogMissingComponent(target, nameof(BuffComponent), "限时护盾");
                return false;
            }

            buff.ApplyShield(value, duration);
            return true;
        }

        /// <summary>
        /// 施加减速。移速的实际写入只经 MovementComponent.SetMoveSpeed（禁止直接改 NavMeshAgent.speed）。
        /// </summary>
        /// <param name="target">被减速方。</param>
        /// <param name="percent">减速比例 0~1。</param>
        /// <param name="duration">持续时长（秒），非正数直接忽略。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplySlow(ITargetable target, float percent, float duration)
        {
            if (percent <= 0f || duration <= 0f)
            {
                return false;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                // 没有 BuffComponent 的单位无法承载状态效果。这属于配置遗漏（不是致命错误），
                // 但必须让使用者看到——否则表现为"技能放了却没效果"且毫无线索。
                LogMissingComponent(target, nameof(BuffComponent), "减速");
                return false;
            }

            buff.ApplySlow(percent, duration);
            return true;
        }

        /// <summary>
        /// 施加眩晕：禁止移动与攻击，到期自动解除。
        /// </summary>
        /// <param name="target">被眩晕方。</param>
        /// <param name="duration">持续时长（秒），非正数直接忽略。</param>
        /// <returns>是否成功施加。</returns>
        public static bool ApplyStun(ITargetable target, float duration)
        {
            if (duration <= 0f)
            {
                return false;
            }

            BuffComponent buff = ResolveBuff(target);
            if (buff == null)
            {
                LogMissingComponent(target, nameof(BuffComponent), "眩晕");
                return false;
            }

            buff.ApplyStun(duration);
            return true;
        }

        /// <summary>
        /// 判断目标当前是否可以被施加效果。
        /// 直接复用 EntityBase.IsValidTarget（全项目唯一的目标合法性入口），不在这里重写规则。
        /// </summary>
        /// <param name="target">待判断目标，允许为 null。</param>
        /// <returns>可用于结算返回 true。</returns>
        public static bool IsUsable(ITargetable target)
        {
            if (target == null)
            {
                return false;
            }

            // 接口引用在 GameObject 被销毁后不会变成 null，必须先做存活检查再访问其属性，
            // 否则会抛 MissingReferenceException（弹道命中瞬间目标被销毁就是这个场景）。
            if (target is UnityEngine.Object unityObject && unityObject == null)
            {
                return false;
            }

            return target.IsValidTarget;
        }

        /// <summary>
        /// 从 ITargetable 解析出 HealthComponent。
        /// 用 GetComponentInParent 而不是强转：本项目 ITargetable 由 EntityBase 实现、IDamageable 由
        /// HealthComponent 实现，两者是同一物体上的不同组件，不能简单地 as 过去。
        /// 本方法与 CombatComponent.ResolveDamageable 是同一套桥接思路（那边返回接口，这边返回具体组件，
        /// 因为护盾需要调用 IDamageable 之外的成员）。
        /// </summary>
        private static HealthComponent ResolveHealth(ITargetable target)
        {
            if (target is Component component)
            {
                return component.GetComponentInParent<HealthComponent>();
            }

            return null;
        }

        /// <summary>从 ITargetable 解析出 BuffComponent（可能为 null，调用方需处理）。</summary>
        private static BuffComponent ResolveBuff(ITargetable target)
        {
            if (target is Component component)
            {
                return component.GetComponentInParent<BuffComponent>();
            }

            return null;
        }

        /// <summary>就"目标缺少承载效果的组件"告警一次。用目标名而不是每帧刷屏。</summary>
        private static void LogMissingComponent(ITargetable target, string componentName, string effectName)
        {
            string targetName = target is Component component ? component.name : "未知目标";
            Debug.LogWarning(
                $"[SkillEffectResolver] {targetName} 上找不到 {componentName}，{effectName}效果未生效。" +
                "请为该预制体补齐组件（一键组装工具会在英雄预制体上自动挂载）。");
        }
    }
}

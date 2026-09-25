using System;
using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Units;

namespace MOBA.Gameplay
{
    /// <summary>
    /// 一次击杀的记录（阶段七）。刻意用 <c>readonly struct</c>：
    /// 它作为事件参数被传递，值类型不会在每次播报时产生一次堆分配（与项目"稳态 GC ≈ 0"的取向一致）。
    /// 所有字段在构造后不可变，订阅方（播报 / 计分板 / 调试视图）只读。
    /// </summary>
    public readonly struct KillRecord
    {
        /// <summary>击杀者；无归属击杀时为 null。</summary>
        public readonly EntityBase Killer;

        /// <summary>被击杀者（本项目的统计口径下必定是英雄）。</summary>
        public readonly EntityBase Victim;

        /// <summary>击杀者阵营；无归属击杀时为 Neutral。</summary>
        public readonly TeamType KillerTeam;

        /// <summary>被击杀者阵营。</summary>
        public readonly TeamType VictimTeam;

        /// <summary>击杀者显示名（已含阵营前缀，可直接拼进播报文案）。</summary>
        public readonly string KillerName;

        /// <summary>被击杀者显示名（已含阵营前缀）。</summary>
        public readonly string VictimName;

        /// <summary>是否存在有效击杀者（false = 阵亡但无人归属，例如环境伤害或被同阵营误伤）。</summary>
        public readonly bool HasKiller;

        /// <summary>构造一条击杀记录。</summary>
        public KillRecord(
            EntityBase killer,
            EntityBase victim,
            TeamType killerTeam,
            TeamType victimTeam,
            string killerName,
            string victimName,
            bool hasKiller)
        {
            Killer = killer;
            Victim = victim;
            KillerTeam = killerTeam;
            VictimTeam = victimTeam;
            KillerName = killerName;
            VictimName = victimName;
            HasKiller = hasKiller;
        }
    }

    /// <summary>
    /// 击杀 / 死亡统计与播报事件源（阶段七，对应计划书 2 节目的 `MatchStatsTracker`）。
    ///
    /// 职责边界：
    /// 1. 只做"记账 + 广播"：谁是击杀者、双方各死了几次、每次击杀产生一条播报事件；
    /// 2. 【唯一计数器】计分板（ScoreboardView）只读本类的值，不自己再数一遍——
    ///    "击杀数与统计完全一致、无重复计数"这条验收因此是结构性保证，而不是靠两处各自算对；
    /// 3. 不判定胜负（那是 MatchController），不播表现（那是 KillFeedView）。
    ///
    /// 【归属判定怎么来的】复用阶段六就为此刻预留的 <see cref="HealthComponent.LastDamageSource"/>：
    /// 它是"本次生命内最后一次伤害的来源"，由 HealthComponent 在每次结算时写入。
    /// 因此本类【不需要】任何新事件，只需要订阅既有的 OnDied 再读这个字段。
    ///
    /// 【为什么只统计英雄（架构师 D3 裁决）】小兵每几秒死一个，纳入统计会让播报刷屏、
    /// 让计分板数字变成"补刀数"的量级，而 README §2.1.5 的语义是"击杀播报 / 双方击杀数"（MOBA 语境下指英雄击杀）。
    /// 扩展点：将来要做补刀统计，在 AddDeath 旁边加一个独立计数即可，播报仍只播英雄。
    /// </summary>
    [DisallowMultipleComponent]
    public class MatchStatsTracker : MonoBehaviour
    {
        /// <summary>
        /// 每发生一次（英雄）击杀广播一次。参数为不可变的记录结构。
        /// 无论是否存在击杀者都会广播——"阵亡"本身也是一条需要播报的信息。
        /// </summary>
        public event Action<KillRecord> OnKillLogged;

        /// <summary>击杀数或死亡数发生变化时广播（计分板据此刷新）。</summary>
        public event Action OnScoreChanged;

        /// <summary>
        /// 实体 → 它 OnDied 的订阅委托。
        /// 必须把委托实例存下来，退订时要用同一个实例（C# 的 -= 按委托相等性匹配）。
        /// </summary>
        private readonly Dictionary<EntityBase, Action> deathHandlers = new Dictionary<EntityBase, Action>();

        private int blueKills;
        private int blueDeaths;
        private int redKills;
        private int redDeaths;

        /// <summary>是否已就"单位没有 HealthComponent"告警过一次。</summary>
        private bool hasReportedMissingHealth;

        /// <summary>取某阵营的击杀数。</summary>
        /// <param name="team">阵营。</param>
        /// <returns>击杀数；中立阵营恒为 0。</returns>
        public int GetKills(TeamType team)
        {
            if (team == TeamType.Player)
            {
                return blueKills;
            }

            if (team == TeamType.Enemy)
            {
                return redKills;
            }

            return 0;
        }

        /// <summary>取某阵营的死亡数。</summary>
        /// <param name="team">阵营。</param>
        /// <returns>死亡数；中立阵营恒为 0。</returns>
        public int GetDeaths(TeamType team)
        {
            if (team == TeamType.Player)
            {
                return blueDeaths;
            }

            if (team == TeamType.Enemy)
            {
                return redDeaths;
            }

            return 0;
        }

        /// <summary>
        /// 阵营的中文名称（阶段七统一命名：玩家方 = 蓝方，敌方 = 红方）。
        /// 播报与计分板都走这一处，避免两处措辞不一致。
        /// 说明：MatchController / MatchResultView 里既有的两处措辞（"玩家方" / "蓝方"）保持不变——
        /// 它们是已验收代码，本轮按 D5「控制风险范围」不动。
        /// </summary>
        /// <param name="team">阵营。</param>
        /// <returns>中文名称。</returns>
        public static string DescribeTeamName(TeamType team)
        {
            if (team == TeamType.Player)
            {
                return "蓝方";
            }

            if (team == TeamType.Enemy)
            {
                return "红方";
            }

            return "中立";
        }

        /// <summary>
        /// 单位的显示名（阶段七统一实现）。
        /// 英雄优先用它的显示名（HeroController.HeroDisplayName），其余单位按类型给中文名。
        /// 刻意【不】用 gameObject.name：那会是 "MinionPrefab(Clone)" 这种带后缀的资产名，不适合出现在播报里。
        /// </summary>
        /// <param name="entity">目标单位。</param>
        /// <returns>显示名。</returns>
        public static string DescribeEntity(EntityBase entity)
        {
            if (entity == null)
            {
                return "未知单位";
            }

            HeroController heroController = entity.GetComponent<HeroController>();
            if (heroController != null)
            {
                return heroController.HeroDisplayName;
            }

            switch (entity.EntityType)
            {
                case EntityType.Minion:
                    return "小兵";
                case EntityType.Tower:
                    return "防御塔";
                case EntityType.Base:
                    return "基地";
                default:
                    return "单位";
            }
        }

        /// <summary>
        /// 订阅注册表事件并补挂已存在的实体（先订阅、再补挂，顺序不可颠倒）。
        /// </summary>
        private void OnEnable()
        {
            EntityRegistry.OnEntityRegistered += HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered += HandleEntityUnregistered;

            EntityBase[] snapshot = EntityRegistry.Snapshot();
            for (int i = 0; i < snapshot.Length; i++)
            {
                HandleEntityRegistered(snapshot[i]);
            }
        }

        /// <summary>退订全部单位的死亡事件。</summary>
        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            foreach (KeyValuePair<EntityBase, Action> pair in deathHandlers)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                HealthComponent health = pair.Key.GetComponent<HealthComponent>();
                if (health != null && pair.Value != null)
                {
                    health.OnDied -= pair.Value;
                }
            }

            deathHandlers.Clear();
        }

        /// <summary>
        /// 实体登记回调：订阅它的死亡事件。
        ///
        /// 【为什么这里用闭包，而飘字那边不用】OnDied 是既有事件（<c>Action</c>，不带发送者），
        /// 且它的签名已被 HeroController / DeadState / BaseCoreController 订阅，不能为了本类改成带发送者的形式。
        /// 代价是每个单位一个闭包（几十字节）。可以接受的理由：死亡是低频事件（每局几十次），
        /// 闭包只在"单位登记"时创建一次，不在每帧路径上。
        /// 与之相对，受伤是高频事件 —— 这正是阶段七为它新增的 OnDamaged 刻意带上发送者的原因。
        /// </summary>
        /// <param name="entity">刚登记的实体。</param>
        private void HandleEntityRegistered(EntityBase entity)
        {
            if (entity == null || deathHandlers.ContainsKey(entity))
            {
                return;
            }

            HealthComponent health = entity.GetComponent<HealthComponent>();
            if (health == null)
            {
                if (!hasReportedMissingHealth)
                {
                    hasReportedMissingHealth = true;
                    Debug.LogWarning(
                        $"[MatchStatsTracker] {entity.name} 上没有 HealthComponent，不会被纳入击杀统计。", entity);
                }

                return;
            }

            EntityBase captured = entity;
            Action handler = () => HandleEntityDied(captured);

            health.OnDied += handler;
            deathHandlers[entity] = handler;
        }

        /// <summary>实体注销回调：退订它的死亡事件。</summary>
        /// <param name="entity">刚注销的实体。</param>
        private void HandleEntityUnregistered(EntityBase entity)
        {
            if (entity == null)
            {
                return;
            }

            Action handler;
            if (!deathHandlers.TryGetValue(entity, out handler))
            {
                return;
            }

            HealthComponent health = entity.GetComponent<HealthComponent>();
            if (health != null && handler != null)
            {
                health.OnDied -= handler;
            }

            deathHandlers.Remove(entity);
        }

        /// <summary>
        /// 死亡回调：做归属判定、累加计数、广播播报事件。
        /// </summary>
        /// <param name="victim">死亡单位。</param>
        private void HandleEntityDied(EntityBase victim)
        {
            if (victim == null)
            {
                return;
            }

            // 统计口径：只统计英雄（架构师 D3 裁决）。小兵/塔/基地的死亡不进播报也不进计分板。
            if (victim.EntityType != EntityType.Hero)
            {
                return;
            }

            HealthComponent health = victim.GetComponent<HealthComponent>();

            // 归属来源：阶段六就为此预留的字段。读不到（组件缺失）时按"无归属"处理。
            EntityBase killer = health != null ? health.LastDamageSource : null;

            // killer != null 走的是 Unity 重载的 == ：已销毁的对象在这里自动被判为 null，
            // 不需要额外的存活检查（这正是本项目把 Unity 对象判空统一交给重载运算符的原因）。
            bool hasKiller = killer != null &&
                             killer != victim &&
                             killer.Team != victim.Team &&
                             killer.Team != TeamType.Neutral;

            TeamType victimTeam = victim.Team;

            AddDeath(victimTeam);

            if (hasKiller)
            {
                AddKill(killer.Team);
            }

            TeamType killerTeam = hasKiller ? killer.Team : TeamType.Neutral;
            string killerName = hasKiller ? DescribeEntity(killer) : string.Empty;
            string victimName = DescribeEntity(victim);

            KillRecord record = new KillRecord(
                killer, victim, killerTeam, victimTeam, killerName, victimName, hasKiller);

            // 先广播播报、再广播计分变化：播报的即时性比计分板更重要（计分板晚一帧没人看得出来）。
            OnKillLogged?.Invoke(record);
            OnScoreChanged?.Invoke();
        }

        /// <summary>累加击杀数。</summary>
        /// <param name="team">击杀者阵营。</param>
        private void AddKill(TeamType team)
        {
            if (team == TeamType.Player)
            {
                blueKills++;
            }
            else if (team == TeamType.Enemy)
            {
                redKills++;
            }
        }

        /// <summary>累加死亡数。</summary>
        /// <param name="team">被击杀者阵营。</param>
        private void AddDeath(TeamType team)
        {
            if (team == TeamType.Player)
            {
                blueDeaths++;
            }
            else if (team == TeamType.Enemy)
            {
                redDeaths++;
            }
        }
    }
}

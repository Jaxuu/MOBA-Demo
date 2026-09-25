using System;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Units
{
    /// <summary>
    /// 基地控制器：对局胜负判定的载体。
    ///
    /// 职责边界（README 3.3 / 计划书阶段四）：
    /// 1. 基地是"不可移动、不可攻击实体"，因此本类【不】调用 CombatComponent，也【不】索敌——
    ///    预制体上不应挂载 CombatComponent / TargetingComponent / MovementComponent；
    /// 2. 只做一件事：监听自己的 HealthComponent.OnDied，向全局广播"某方基地被摧毁"；
    /// 3. 不判断胜负、不停止出兵、不弹结算 UI——那是 MatchController 的职责。
    ///    本类只负责"把事件说出来"，谁关心谁订阅，避免基地反向依赖对局管理层（否则会形成双向耦合）。
    ///
    /// 为什么不挂 EntityAIController：基地既不能移动也没有攻击能力，FSM 的四个战斗状态对它全部无意义。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class BaseCoreController : MonoBehaviour
    {
        /// <summary>
        /// 基地被摧毁的全局广播，参数为被摧毁基地的阵营（TeamType.Player 或 TeamType.Enemy）。
        ///
        /// 用静态事件而不是实例事件的原因：订阅方（MatchController）需要在开局前就订阅"任意一方基地"的死亡，
        /// 而实例事件要求它先拿到两个基地的引用；静态事件让基地成为纯粹的"广播源"，订阅方无需持有引用。
        ///
        /// 【订阅方必须记得退订】静态事件的委托链不会随订阅者销毁而自动清理，
        /// 若订阅方（如 MatchController）不主动 -=，跨对局会累积重复回调。
        /// </summary>
        public static event Action<TeamType> OnBaseDestroyed;

        [Header("调试")]
        [Tooltip("在 Console 输出基地被摧毁的记录（只输出一次）。")]
        [SerializeField] private bool logDeathEvents = true;

        /// <summary>实体身份入口，阵营从这里读取（阵营只允许有一份数据，不在本类另存字段）。</summary>
        private EntityBase entity;

        /// <summary>生命组件。订阅 OnDied 用，必须存在。</summary>
        private HealthComponent health;

        /// <summary>死亡处理是否已执行，保证"事件回调"与"订阅时已死亡"两条路径合计只执行一次。</summary>
        private bool hasHandledDeath;

        /// <summary>所属阵营（只读），供调试视图与 MatchController 读取。</summary>
        public TeamType Team => entity != null ? entity.Team : TeamType.Neutral;

        /// <summary>是否已被摧毁（只读）。</summary>
        public bool IsDestroyed => hasHandledDeath;

        /// <summary>
        /// 只取实体身份。
        /// 不在 Awake 读 entity.Health：同一 GameObject 上组件的 Awake 顺序不确定，
        /// 且代码动态创建时其他组件可能尚未挂载，属性里会是 null。组件解析统一放 Start。
        /// </summary>
        private void Awake()
        {
            entity = GetComponent<EntityBase>();
        }

        /// <summary>解析依赖并订阅死亡事件（所有 Awake 都保证先于任何 Start 执行，此时组件必定挂载完成）。</summary>
        private void Start()
        {
            if (entity == null)
            {
                Debug.LogError(
                    $"[BaseCoreController] {name} 上找不到 EntityBase，无法取得阵营，胜负判定事件永远不会广播。" +
                    "请确保 EntityBase 与 BaseCoreController 挂在同一个 GameObject 上。", this);
                enabled = false;
                return;
            }

            // 直接 GetComponent 取，不读 entity.Health 属性——理由同 TowerController.ResolveComponents：
            // EntityBase.Start 可能晚于本组件的 Start，届时其缓存属性尚未补全。
            health = entity.GetComponent<HealthComponent>();

            if (health == null)
            {
                Debug.LogError(
                    $"[BaseCoreController] {name} 未挂载 HealthComponent，基地生命无法归零，" +
                    "对局将永远无法判定胜负。请检查预制体配置。", this);
                enabled = false;
                return;
            }

            health.OnDied += HandleDied;

            // 订阅时可能已经死亡（代码创建后立刻被摧毁，或场景中预置了"已被摧毁的基地"）：
            // 那种情况下 OnDied 早已广播过，订阅不会补发，必须主动补一次，否则胜负判定会永久丢失。
            if (health.IsDead)
            {
                HandleDied();
            }
        }

        /// <summary>
        /// 死亡处理：退订 → 取出阵营 → 广播对局结束事件。
        /// 广播放在最后一步，因为订阅方（MatchController）很可能在回调里立刻停止出兵、停止战斗、弹出结算 UI，
        /// 本方法返回后不应再依赖任何自身状态。
        /// </summary>
        private void HandleDied()
        {
            if (hasHandledDeath)
            {
                return;
            }

            hasHandledDeath = true;

            // 先退订再广播：广播过程中若订阅方做出任何可能再次触发本回调的动作，也不会造成重入。
            if (health != null)
            {
                health.OnDied -= HandleDied;
            }

            TeamType destroyedTeam = entity != null ? entity.Team : TeamType.Neutral;

            if (logDeathEvents)
            {
                Debug.Log($"[BaseCoreController] {name}（阵营 {destroyedTeam}）已被摧毁，广播 OnBaseDestroyed。", this);
            }

            OnBaseDestroyed?.Invoke(destroyedTeam);
        }

        /// <summary>兜底退订，避免 HealthComponent 持有已销毁组件的引用。</summary>
        private void OnDestroy()
        {
            if (health != null)
            {
                health.OnDied -= HandleDied;
            }
        }

        /// <summary>
        /// 重置静态事件。
        ///
        /// 为什么必须写这段：静态事件的生命周期属于"类型"，而不是"实例"。
        /// 在 Project Settings → Editor → Enter Play Mode Options 中关闭 Domain Reload 时，
        /// 退出播放模式不会重新加载程序集，上一局 MatchController 的订阅会残留在委托链上，
        /// 下一局基地死亡时会触发两次（第二次还指向已销毁的对象，直接抛 MissingReferenceException）。
        /// SubsystemRegistration 阶段早于任何场景加载与 Awake，是官方推荐的静态状态重置时机。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            OnBaseDestroyed = null;
        }
    }
}

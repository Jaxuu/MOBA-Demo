using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.UI
{
    /// <summary>
    /// 伤害飘字管理器（阶段七）：订阅全部单位的受伤事件、池化生成飘字，并在一个循环里驱动它们。
    ///
    /// 【为什么飘字放在"共享的世界空间画布"下，而不是每条飘字一个 Canvas】
    /// 每个 World Space Canvas 都是独立的渲染批次（无法跨 Canvas 合批）：
    /// 32 条飘字 = 32 个 Canvas = 32 次 draw call + 32 次朝向更新。
    /// 改成"一个世界画布 + 若干子 Text"之后：全部飘字合并为 1 次 draw call、画布只朝向一次。
    /// 语义上也是对的——飘字是"永远面向相机、不参与世界遮挡排序"的元素，本来就属于同一个平面。
    ///
    /// 【为什么不用屏幕空间（Overlay）画布】飘字必须"钉在世界里的受击点"：
    /// 相机平移（中键拖拽）或缩放时，飘字要留在原地而不是跟着屏幕走。
    /// 屏幕空间方案每帧都要把世界点重投影回屏幕，本质是在模拟世界空间，白做一层且更容易出偏差。
    ///
    /// 【执行顺序】[DefaultExecutionOrder(100)] 保证晚于 CameraController 的 LateUpdate 执行，
    /// 否则画布会按上一帧的相机朝向摆放（缩放时表现为飘字轻微歪斜）。
    ///
    /// 【每次伤害恰好一条飘字】口径：伤害被完全吸收时画灰蓝的"吸收 N"；
    /// 否则画红色的实际扣减量（partial 吸收时只画扣血部分，不额外再画一条）。
    /// 这样"每次伤害一条飘字"与"数值与实际扣血量一致"两条验收标准同时成立。
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class DamagePopupManager : MonoBehaviour
    {
        [Header("对象池")]
        [Tooltip("飘字模板（场景中的非激活对象，由一键组装工具生成）。")]
        [SerializeField] private DamagePopupView popupTemplate;

        [Tooltip("池对象的父节点。")]
        [SerializeField] private Transform poolHost;

        [Tooltip("预生成数量，同时也是池容量上限。")]
        [Min(0)]
        [SerializeField] private int prewarmCount = 32;

        [Header("相机与画布")]
        [Tooltip("共享世界空间画布的根节点。每帧把它的朝向复制成相机朝向（billboard）。")]
        [SerializeField] private Transform popupCanvasRoot;

        [Tooltip("主相机。留空时自动取 Camera.main。")]
        [SerializeField] private Camera targetCamera;

        [Header("表现参数")]
        [Tooltip("飘字相对单位根节点的生成高度（米）。统一取一个略高于单位的高度即可，不必逐单位测量。")]
        [Min(0f)]
        [SerializeField] private float popupAnchorHeight = 1.8f;

        [Header("调试")]
        [Tooltip("在 Console 输出飘字订阅记录（排查「某个单位没有飘字」时打开）。")]
        [SerializeField] private bool logLifecycle = false;

        /// <summary>实体 → 它的生命组件。注销时要退订，必须持有同一个组件引用。</summary>
        private readonly Dictionary<EntityBase, HealthComponent> tracked =
            new Dictionary<EntityBase, HealthComponent>();

        /// <summary>对象池。Awake 创建，早于 OnEnable 的订阅。</summary>
        private PrefabPool<DamagePopupView> pool;

        /// <summary>是否已就"单位没有 HealthComponent"告警过一次。</summary>
        private bool hasReportedMissingHealth;

        /// <summary>当前正在播放的飘字数量（只读），供调试使用。</summary>
        public int ActivePopupCount => pool != null ? pool.ActiveCount : 0;

        /// <summary>建立对象池并校验配置。</summary>
        private void Awake()
        {
            if (popupTemplate == null || poolHost == null)
            {
                Debug.LogError(
                    $"[DamagePopupManager] {name} 未配置飘字模板或池宿主，伤害飘字将完全不可用。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                enabled = false;
                return;
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            pool = new PrefabPool<DamagePopupView>(popupTemplate, poolHost, prewarmCount);
        }

        /// <summary>
        /// 订阅注册表事件并补挂已存在的实体（先订阅、再补挂，顺序不可颠倒，理由见 EntityRegistry 的注释）。
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

        /// <summary>退订并把全部飘字归还池中。</summary>
        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            // 退订所有单位的受伤事件：漏掉这一步，已禁用的管理器会继续接收伤害回调并试图生成飘字。
            foreach (KeyValuePair<EntityBase, HealthComponent> pair in tracked)
            {
                if (pair.Value != null)
                {
                    pair.Value.OnDamaged -= HandleDamaged;
                }
            }

            tracked.Clear();

            if (pool != null)
            {
                pool.ReleaseAll();
            }
        }

        /// <summary>
        /// 每帧：先把共享画布朝向相机（一次），再驱动全部飘字。
        /// 顺序很重要——先定画布朝向、再写各飘字的世界坐标，飘字当帧就会落到正确的位置上。
        /// </summary>
        private void LateUpdate()
        {
            if (pool == null)
            {
                return;
            }

            if (popupCanvasRoot != null)
            {
                if (targetCamera == null)
                {
                    targetCamera = Camera.main;
                }

                if (targetCamera != null)
                {
                    popupCanvasRoot.rotation = targetCamera.transform.rotation;
                }
            }

            List<DamagePopupView> activePopups = pool.ActiveItems;
            float deltaTime = Time.deltaTime;

            // 倒序遍历：Tick 返回 true 时会在循环内归还（从列表末尾移除），倒序可保证索引不失效。
            for (int i = activePopups.Count - 1; i >= 0; i--)
            {
                DamagePopupView popup = activePopups[i];
                if (popup == null)
                {
                    continue;
                }

                if (popup.Tick(deltaTime))
                {
                    pool.Release(popup);
                }
            }
        }

        /// <summary>
        /// 实体登记回调：订阅它的受伤事件。
        /// 【为什么这里是同一个回调而不是每个单位一个闭包】伤害是高频事件，
        /// 而 OnDamaged 的第一个参数就是发送者（HealthComponent），
        /// 因此可以用一个无捕获的方法订阅所有单位 —— 每次订阅零堆分配。
        /// </summary>
        /// <param name="entity">刚登记的实体。</param>
        private void HandleEntityRegistered(EntityBase entity)
        {
            if (entity == null || pool == null)
            {
                return;
            }

            if (tracked.ContainsKey(entity))
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
                        $"[DamagePopupManager] {entity.name} 上没有 HealthComponent，不会为它显示伤害飘字。", entity);
                }

                return;
            }

            health.OnDamaged += HandleDamaged;
            tracked[entity] = health;

            if (logLifecycle)
            {
                Debug.Log($"[DamagePopupManager] 已订阅 {entity.name} 的受伤事件（当前 {tracked.Count} 个）。", this);
            }
        }

        /// <summary>实体注销回调：退订它的受伤事件（物体销毁前必定先走 OnDisable，因此这里退订是安全的）。</summary>
        /// <param name="entity">刚注销的实体。</param>
        private void HandleEntityUnregistered(EntityBase entity)
        {
            if (entity == null)
            {
                return;
            }

            HealthComponent health;
            if (!tracked.TryGetValue(entity, out health))
            {
                return;
            }

            if (health != null)
            {
                health.OnDamaged -= HandleDamaged;
            }

            tracked.Remove(entity);

            if (logLifecycle)
            {
                Debug.Log($"[DamagePopupManager] 已退订 {entity.name} 的受伤事件（当前 {tracked.Count} 个）。", this);
            }
        }

        /// <summary>
        /// 受伤回调：生成一条飘字。
        /// 参数口径见 HealthComponent.OnDamaged 的注释（第一个浮点是"实际扣减的生命值"）。
        /// </summary>
        /// <param name="victim">受伤单位（发送者）。</param>
        /// <param name="healthDamage">本次实际扣减的生命值。</param>
        /// <param name="shieldAbsorbed">本次被护盾吸收的伤害。</param>
        /// <param name="source">伤害来源（飘字用不到，保留是为了与事件契约一致）。</param>
        private void HandleDamaged(HealthComponent victim, float healthDamage, float shieldAbsorbed, EntityBase source)
        {
            if (pool == null || victim == null)
            {
                return;
            }

            bool fullyAbsorbed = healthDamage <= 0f && shieldAbsorbed > 0f;

            // 既没扣血也没有护盾吸收 → 无事发生（防御性兜底，正常不会走到）。
            if (healthDamage <= 0f && shieldAbsorbed <= 0f)
            {
                return;
            }

            DamagePopupView popup = pool.Get();

            // 池耗尽 → 这条飘字不显示。表现缺失不影响逻辑（池内部已告警一次）。
            if (popup == null)
            {
                return;
            }

            Vector3 anchor = victim.transform.position + Vector3.up * popupAnchorHeight;

            if (fullyAbsorbed)
            {
                popup.Play(anchor, BuildShieldText(shieldAbsorbed), true);
            }
            else
            {
                popup.Play(anchor, BuildDamageText(healthDamage), false);
            }
        }

        /// <summary>
        /// 格式化伤害数字：整数就显示整数（"25"），带小数才显示一位小数（"12.5"）。
        /// 【关于分配】这里每次伤害会产生一个短字符串，属于"每次事件固有分配"，
        /// 与阶段六弹道 Instantiate 同级；战斗稳态（没有伤害发生时）为 0 分配。
        /// 若将来要彻底归零，需要引入数字位图或字符缓存，收益远不及复杂度。
        /// </summary>
        /// <param name="damage">伤害值。</param>
        /// <returns>用于显示的文本。</returns>
        private static string BuildDamageText(float damage)
        {
            float rounded = Mathf.Round(damage);
            return Mathf.Abs(damage - rounded) < 0.05f
                ? rounded.ToString("0")
                : damage.ToString("F1");
        }

        /// <summary>格式化"护盾吸收"文本，例如「吸收 120」。</summary>
        /// <param name="absorbed">被护盾吸收的伤害。</param>
        /// <returns>用于显示的文本。</returns>
        private static string BuildShieldText(float absorbed)
        {
            float rounded = Mathf.Round(absorbed);
            string number = Mathf.Abs(absorbed - rounded) < 0.05f
                ? rounded.ToString("0")
                : absorbed.ToString("F1");

            return "吸收 " + number;
        }
    }
}

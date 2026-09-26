using UnityEngine;
using MOBA.Components;

namespace MOBA.VFX
{
    /// <summary>
    /// 白盒占位特效的自生命周期组件（表现层）：挂在每个由 <see cref="WhiteboxVfxManager"/> 生成的
    /// 临时几何体上，负责"什么时候把它收掉"，以及可选的跟随与下落表现。
    ///
    /// 【为什么需要一个独立组件而不是在管理器里维护一张表】
    /// 占位特效是"生成即忘"的：管理器只管生成，回收条件却有好几种（到时 / 护盾消失 / 宿主销毁）。
    /// 把这些条件写进管理器，管理器就要为每个特效记一份状态并每帧遍历 —— 而特效自己知道自己挂了多久、
    /// 跟着谁、给谁做护盾。让特效自己管自己，管理器在 OnDisable 里"整棵子树一起清掉"即可（层级即账本）。
    ///
    /// 【为什么用 Update 递减而不是 Destroy(gameObject, delay)】
    /// 因为"护盾消失"与"宿主销毁"这两条回收条件是延迟销毁表达不了的；统一放在 Update 里，
    /// 三种回收条件共用同一个出口，不会出现"某条路径忘了回收"。
    ///
    /// 【关于是否属于逻辑层】本组件只写自己的 Transform 与自己的存亡，不读也不改任何战斗数值；
    /// 关掉整个特效系统，对局结果完全不变（README §3.4 铁律 4）。
    /// </summary>
    [DisallowMultipleComponent]
    public class WhiteboxVfxLifetime : MonoBehaviour
    {
        [Header("清理")]
        [Tooltip("自动销毁倒计时（秒）。<= 0 表示不按时间回收，只靠「护盾消失 / 宿主销毁」回收。")]
        [SerializeField] private float lifetime = 2f;

        [Header("跟随（可选）")]
        [Tooltip("跟随目标：每帧把特效摆到它的位置 + 偏移。留空 = 不跟随（固定在生成点）。")]
        [SerializeField] private Transform followTarget;

        [Tooltip("跟随偏移（相对跟随目标的世界空间向量）。")]
        [SerializeField] private Vector3 followOffset = Vector3.zero;

        [Header("条件回收（可选）")]
        [Tooltip("护盾来源：它的护盾降到 0 时立刻回收（用于「护盾球持续到护盾消失」）。")]
        [SerializeField] private HealthComponent shieldSource;

        [Tooltip("宿主根节点：它被销毁时立刻回收（避免特效留在空场上）。")]
        [SerializeField] private Transform ownerRoot;

        [Header("下落动画（可选，用于「巨剑从天而降」）")]
        [Tooltip("生成时相对落点抬高多少米；0 = 不做下落动画。")]
        [SerializeField] private float fallFromHeight = 0f;

        [Tooltip("下落耗时（秒）。")]
        [SerializeField] private float fallDuration = 0.25f;

        /// <summary>生成时刻（Time.time 基准）。用于倒计时与下落进度。</summary>
        private float spawnTime;

        /// <summary>落点（世界坐标）。下落动画的终点。</summary>
        private Vector3 anchorPosition;

        /// <summary>是否正在下落（落到底之后不再每帧写位置）。</summary>
        private bool isFalling;

        /// <summary>是否配置了宿主（用于区分"从未配置"与"配置的对象已被销毁"）。</summary>
        private bool hasOwner;

        /// <summary>是否配置了护盾来源（理由同 hasOwner）。</summary>
        private bool hasShieldSource;

        /// <summary>延迟出现时长（秒）。0 = 生成即出现。</summary>
        private float startDelay;

        /// <summary>出现时刻（Time.time 基准）。下落动画的进度以它为起点，而不是生成时刻。</summary>
        private float appearTime;

        /// <summary>是否正处于"已生成但还没出现"的状态。</summary>
        private bool isHidden;

        /// <summary>
        /// 配置。必须在 AddComponent 之后立刻调用（AddComponent 会同步触发 Awake，
        /// 而 Awake 里读不到这些字段 —— 与项目「动态创建必须先挂齐组件再注入」的约定一致）。
        /// </summary>
        /// <param name="newLifetime">自动销毁倒计时（秒），&lt;= 0 表示不按时间回收。</param>
        /// <param name="newFollowTarget">跟随目标，允许为 null。</param>
        /// <param name="newFollowOffset">跟随偏移。</param>
        /// <param name="newShieldSource">护盾来源，允许为 null。</param>
        /// <param name="newOwnerRoot">宿主根节点，允许为 null。</param>
        /// <param name="newFallFromHeight">下落起始高度（米），0 = 不做下落动画。</param>
        /// <param name="newFallDuration">下落耗时（秒）。</param>
        public void Configure(
            float newLifetime,
            Transform newFollowTarget,
            Vector3 newFollowOffset,
            HealthComponent newShieldSource,
            Transform newOwnerRoot,
            float newFallFromHeight,
            float newFallDuration)
        {
            spawnTime = Time.time;
            appearTime = spawnTime;

            lifetime = newLifetime;
            followTarget = newFollowTarget;
            followOffset = newFollowOffset;
            shieldSource = newShieldSource;
            ownerRoot = newOwnerRoot;
            hasOwner = newOwnerRoot != null;
            hasShieldSource = newShieldSource != null;

            fallFromHeight = Mathf.Max(0f, newFallFromHeight);
            fallDuration = Mathf.Max(0f, newFallDuration);

            // 落点取"配置时刻的位置"：之后即使跟随目标移动，下落动画也仍然从生成点垂直落下来。
            anchorPosition = transform.position;
            isFalling = fallFromHeight > 0f;

            if (isFalling)
            {
                transform.position = anchorPosition + Vector3.up * fallFromHeight;
            }
        }

        /// <summary>
        /// 设置"延迟出现"时长（秒）。必须在 <see cref="Configure"/> 之后调用。
        ///
        /// 【为什么需要它 —— 这是弹道类技能的表现正确性问题】
        /// 指向性技能的 OnSpellReleased 是在【弹道生成的那一刻】广播的，而伤害要等弹道飞完才结算。
        /// 若特效立刻出现，画面会变成"巨柱先砸下来、半秒后伤害数字才跳"，观感上技能是"空的"。
        /// 把延迟设成「施法者到目标的距离 / 弹道速度」，表现就与结算时刻对齐了。
        ///
        /// 【实现方式】延迟期间把渲染器全部关掉（对象仍在场，只是看不见）：
        /// 这比"延迟一段时间后再 Instantiate"更简单，也不会因为延迟期间施法者阵亡而漏掉清理
        /// （条件回收在 Update 里先于"是否出现"判断，因此隐形柱子照样会被回收）。
        /// </summary>
        /// <param name="delay">延迟秒数，非正数表示不延迟。</param>
        public void SetStartDelay(float delay)
        {
            startDelay = Mathf.Max(0f, delay);

            if (startDelay <= 0f)
            {
                return;
            }

            appearTime = spawnTime + startDelay;
            isHidden = true;

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = false;
                }
            }
        }

        /// <summary>延迟结束：把渲染器打开，并把下落动画的起点对齐到"出现这一刻"。</summary>
        private void Reveal()
        {
            isHidden = false;
            appearTime = Time.time;

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = true;
                }
            }

            // 下落动画从"出现"开始算，因此此刻把柱子重新摆回起始高度。
            if (fallFromHeight > 0f)
            {
                isFalling = true;
                transform.position = anchorPosition + Vector3.up * fallFromHeight;
            }
        }

        private void Update()
        {
            // ---- 条件回收 ①：宿主（施法者 / 目标）已被销毁 ----
            // 用 hasOwner 而不是 ownerRoot != null 判断"是否配置过"：Unity 重载的 == 运算符
            // 对【已销毁】的对象同样返回 true，直接用 null 判断会把"从未配置"误判成"已销毁"。
            if (hasOwner && ownerRoot == null)
            {
                Destroy(gameObject);
                return;
            }

            // ---- 条件回收 ②：护盾已消失（被吸收完 / 到期清除 / 宿主阵亡） ----
            // 注意判据用 hasShieldSource 而不是 shieldSource != null：
            // Unity 重载的 == 对【已销毁】对象也返回 null，所以"护盾来源被销毁"应当也算护盾没了。
            if (hasShieldSource && (shieldSource == null || shieldSource.CurrentShield <= 0f))
            {
                Destroy(gameObject);
                return;
            }

            // ---- 延迟出现：还没到点就什么都不做（连跟随也不做，避免隐形物被"提前"摆位） ----
            if (isHidden)
            {
                if (Time.time < appearTime)
                {
                    return;
                }

                Reveal();
            }

            // ---- 跟随 ----
            if (followTarget != null)
            {
                transform.position = followTarget.position + followOffset;
                isFalling = false;
            }

            // ---- 下落动画（每帧只做一次位置写入，落到底即停） ----
            if (isFalling)
            {
                float progress = fallDuration > 0f
                    ? Mathf.Clamp01((Time.time - appearTime) / fallDuration)
                    : 1f;

                transform.position = anchorPosition + Vector3.up * (fallFromHeight * (1f - progress));

                if (progress >= 1f)
                {
                    isFalling = false;
                }
            }

            // ---- 条件回收 ③：到时 ----
            if (lifetime > 0f && Time.time - spawnTime >= lifetime)
            {
                Destroy(gameObject);
            }
        }
    }
}

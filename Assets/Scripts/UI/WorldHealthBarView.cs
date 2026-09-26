using UnityEngine;
using UnityEngine.UI;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.UI
{
    /// <summary>
    /// 头顶世界空间血条（阶段七）：单个单位的血条视图，由 <see cref="WorldHealthBarManager"/> 统一取用与归还。
    ///
    /// 职责边界：
    /// 1. 只做"显示"：数值全部来自 HealthComponent 的事件（OnHealthChanged / OnShieldChanged / OnDied），
    ///    本类【不】写任何战斗数值，也【不】参与目标合法性判断；
    /// 2. 不自己决定"该不该显示"：挂载与回收由管理器负责（订阅 EntityRegistry 的登记/注销事件），
    ///    本类只回答"我这一条现在该不该画"；
    /// 3. 没有 Update / LateUpdate：位置、朝向、缩放由管理器在它的 LateUpdate 里统一驱动
    ///    （24 条血条 = 1 个循环，而不是 24 个 MonoBehaviour 的每帧回调）。
    ///
    /// 【为什么数值绝不轮询】README 明确要求「由 OnHealthChanged 事件驱动更新，禁止每帧轮询」。
    /// 每帧只写 Transform（位置/旋转/缩放），不读任何战斗数据 —— 这是"UI 稳态 GC ≈ 0"的前提。
    ///
    /// 【恒定屏幕像素高度】验收要求"血条位置与实体头顶偏差 < 5px（1080p）"。
    /// 若血条的世界尺寸固定，滚轮拉远后它会缩成一条线、拉近后又糊满屏幕，那条验收就没有意义了。
    /// 因此每帧按「与相机的距离」重设缩放，使它在屏幕上的像素高度恒定（公式见 UpdateVisual）。
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldHealthBarView : MonoBehaviour
    {
        [Header("子元素（由一键组装工具注入）")]
        [Tooltip("血条自身的 Canvas。隐藏血条靠禁用它（而不是禁用本物体），\n" +
                 "因为本物体被禁用会触发 OnDisable → 自动解绑，血条会彻底脱离单位。")]
        [SerializeField] private Canvas barCanvas;

        [Tooltip("血量填充条（Image.type = Filled / Horizontal）。")]
        [SerializeField] private Image fillImage;

        [Tooltip("护盾覆盖条（Image.type = Filled / Horizontal），护盾为 0 时自动隐藏。")]
        [SerializeField] private Image shieldImage;

        [Header("配色（README：敌方红 / 友方绿）")]
        [SerializeField] private Color allyFillColor = new Color(0.25f, 0.85f, 0.35f, 1f);
        [SerializeField] private Color enemyFillColor = new Color(0.9f, 0.25f, 0.25f, 1f);
        [SerializeField] private Color neutralFillColor = new Color(0.85f, 0.85f, 0.85f, 1f);

        [Header("尺寸")]
        [Tooltip("血条在屏幕上的目标像素高度（以当前屏幕高度为基准）。")]
        [Min(1f)]
        [SerializeField] private float pixelHeight = 12f;

        [Tooltip("缩放为 1 时血条的世界高度（米）。必须等于模板 Canvas 的 RectTransform 高度，否则像素高度会整体偏移。")]
        [Min(0.001f)]
        [SerializeField] private float rectWorldHeight = 0.12f;

        [Tooltip("缩放夹取范围：防止极端距离下血条缩成一个点或糊满屏幕。")]
        [SerializeField] private float minScale = 0.2f;

        [SerializeField] private float maxScale = 8f;

        [Header("显示策略")]
        // 【为什么默认值是 false —— 实机踩出来的"英雄没有血条"】
        // 本字段此前默认 true（满血即隐藏），而英雄的复活链路会把生命填满（HealthComponent.Revive），
        // 于是"刚复活 / 刚回满血"的英雄在画面上【一条血条都没有】，看起来就像英雄压根没挂血条，
        // 而小兵因为长期带伤、血条常在，于是被误判成"只有小兵有血条"的配置问题。
        //
        // 这里同时纠正一条被写错的注释：README 对血条的要求只有「跟随头顶 / 敌我配色 / billboard / 池化复用」，
        // 并没有"满血隐藏"这一条（见 README 阶段七验收表）。白盒测试期最重要的恰恰是
        // 「一眼看清场上有几个单位、谁是谁」，因此让血条常显才是正确取舍。
        // 需要恢复旧观感时，把本字段在 Inspector 里勾上即可（或改一键组装工具的注入值）。
        [Tooltip("满血时隐藏血条。默认【关闭】—— 白盒期需要每个单位都常显血条；\n" +
                 "开启后满血单位（含刚复活、刚回满血的英雄）在画面上完全没有血条，容易被误判成「英雄没挂血条」。")]
        [SerializeField] private bool hideWhenFull = false;

        [Tooltip("死亡时隐藏（尸体上留一条空血条没有意义）。")]
        [SerializeField] private bool hideWhenDead = true;

        [Tooltip("单位没有碰撞体时使用的兜底锚点高度（米）。正常单位会按碰撞体包围盒顶部自动测量。")]
        [Min(0f)]
        [SerializeField] private float defaultAnchorHeight = 1.5f;

        /// <summary>当前绑定的实体（只读）。管理器据此判断"这个单位是否已经有血条"。</summary>
        private EntityBase boundEntity;

        /// <summary>当前绑定的生命组件，退订时要用同一个引用。</summary>
        private HealthComponent boundHealth;

        /// <summary>目标相机。billboard 与恒定像素高度都依赖它。</summary>
        private Camera boundCamera;

        /// <summary>血条相对单位根节点的高度偏移（绑定时一次性测量，之后每帧只做加法）。</summary>
        private float anchorOffsetY = 1.5f;

        /// <summary>缓存的当前生命百分比（0~1）。事件驱动更新，每帧只读它。</summary>
        private float healthPercent = 1f;

        /// <summary>缓存的最大生命值，用于把护盾值换算成条宽比例。</summary>
        private float maxHealth = 1f;

        /// <summary>是否已死亡。死亡后不再显示（可按配置保留）。</summary>
        private bool isDead;

        /// <summary>是否已绑定实体。未绑定时 UpdateVisual 直接返回。</summary>
        private bool isBound;

        /// <summary>上一帧的可见性，用于避免每帧重复写 Canvas.enabled。</summary>
        private bool isVisible = true;

        /// <summary>是否已就"单位没有碰撞体、改用兜底锚点高度"告警过一次。</summary>
        private bool hasReportedMissingAnchor;

        /// <summary>当前绑定的实体（只读）。</summary>
        public EntityBase BoundEntity => boundEntity;

        /// <summary>是否已绑定实体（只读）。</summary>
        public bool IsBound => isBound;

        /// <summary>
        /// 绑定到一个实体：测量锚点、设定配色、订阅生命事件，并主动读一次当前值。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        /// <param name="camera">主相机，用于 billboard 与像素高度换算。</param>
        /// <param name="allyTeam">"我方"阵营，用于决定血条配色（友方绿 / 敌方红）。</param>
        /// <returns>绑定成功返回 true；实体没有 HealthComponent 时返回 false（调用方负责告警）。</returns>
        public bool Bind(EntityBase entity, Camera camera, TeamType allyTeam)
        {
            if (entity == null)
            {
                return false;
            }

            // 先解绑旧目标：池对象可能带着上一轮的订阅与状态，不清掉会出现"一条血条同时跟两个单位"。
            Unbind();

            HealthComponent health = entity.GetComponent<HealthComponent>();
            if (health == null)
            {
                return false;
            }

            boundEntity = entity;
            boundHealth = health;
            boundCamera = camera;
            isBound = true;
            isDead = false;

            anchorOffsetY = MeasureAnchorHeight(entity);
            ApplyFillColor(allyTeam);

            health.OnHealthChanged += HandleHealthChanged;
            health.OnShieldChanged += HandleShieldChanged;
            health.OnDied += HandleDied;

            // 订阅后主动读一次当前值：单位可能在血条挂上之前就已经初始化完（甚至已经受过伤），
            // 事件不会补发（与 HeroController「订阅时已死亡」的补偿路径同一思路）。
            HandleHealthChanged(health.CurrentHealth, health.MaxHealth);
            HandleShieldChanged(health.CurrentShield);

            return true;
        }

        /// <summary>
        /// 解绑：退订全部事件并清空状态。幂等，由管理器的归还路径与 OnDisable 各调用一次也安全。
        /// </summary>
        public void Unbind()
        {
            if (boundHealth != null)
            {
                boundHealth.OnHealthChanged -= HandleHealthChanged;
                boundHealth.OnShieldChanged -= HandleShieldChanged;
                boundHealth.OnDied -= HandleDied;
            }

            boundEntity = null;
            boundHealth = null;
            boundCamera = null;
            isBound = false;
            isDead = false;
            healthPercent = 1f;
            maxHealth = 1f;
        }

        /// <summary>
        /// 每帧由管理器调用：摆位、朝向相机、按距离重设缩放、刷新显隐。
        ///
        /// 【为什么朝向用"复制相机旋转"而不是 LookAt】血条只需要"和相机同向"（billboard），
        /// 不需要"注视相机"；复制旋转是 4 次赋值，LookAt 要额外做一次正交化与三角函数。
        /// 而且所有血条共用同一个旋转值，不会出现某条因为浮点误差轻微歪头。
        /// </summary>
        /// <param name="camera">主相机；为 null 时回落到绑定时缓存的相机。</param>
        public void UpdateVisual(Camera camera)
        {
            if (!isBound || boundEntity == null)
            {
                return;
            }

            Camera cam = camera != null ? camera : boundCamera;
            if (cam == null)
            {
                return;
            }

            Vector3 anchor = boundEntity.transform.position + Vector3.up * anchorOffsetY;

            transform.position = anchor;
            transform.rotation = cam.transform.rotation;

            // 恒定屏幕像素高度：
            //   透视：屏幕像素高度 = 世界高度 × 屏幕高度 / (2 × 距离 × tan(fov/2))
            //         → 反解出"要让像素高度等于 pixelHeight，世界高度该是多少"
            //   正交：世界高度 = pixelHeight × (2 × orthographicSize) / 屏幕高度（与距离无关）
            float distance = Vector3.Distance(cam.transform.position, anchor);
            float worldHeight;

            if (cam.orthographic)
            {
                worldHeight = pixelHeight * 2f * cam.orthographicSize / Mathf.Max(1f, Screen.height);
            }
            else
            {
                float halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                worldHeight = pixelHeight * 2f * halfFovTan * distance / Mathf.Max(1f, Screen.height);
            }

            float scale = Mathf.Clamp(
                worldHeight / Mathf.Max(0.0001f, rectWorldHeight), minScale, maxScale);

            transform.localScale = new Vector3(scale, scale, scale);

            ApplyVisibility(cam, anchor);
        }

        /// <summary>
        /// 生命周期收尾：被池归还（SetActive(false)）或物体被禁用时自动解绑。
        /// 这是"池只需要 SetActive 就能安全复用"的关键 —— 订阅不会跟着池对象跑到下一个单位身上。
        /// </summary>
        private void OnDisable()
        {
            Unbind();
        }

        /// <summary>
        /// 按碰撞体包围盒顶部测量血条锚点高度（相对单位根节点）。
        ///
        /// 【为什么绑定时测一次就够了】包围盒只在绑定时读一次，运行期每帧只做一次加法；
        /// 同时也避免了"按 EntityType 维护一张高度表"——阶段八换成真实模型后，锚点会自动跟着碰撞体走。
        /// 这里会分配一个 Collider 数组，但它发生在"每个单位绑定一次"的时刻，不在每帧路径上。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        /// <returns>锚点相对根节点的高度偏移（米）。</returns>
        private float MeasureAnchorHeight(EntityBase entity)
        {
            // includeInactive: true —— 单位的碰撞体可能挂在暂时未激活的子节点上，漏掉它们会量到错误高度。
            Collider[] colliders = entity.GetComponentsInChildren<Collider>(true);

            if (colliders == null || colliders.Length == 0)
            {
                if (!hasReportedMissingAnchor)
                {
                    hasReportedMissingAnchor = true;
                    Debug.LogWarning(
                        $"[WorldHealthBarView] {entity.name} 及其子节点上找不到任何 Collider，" +
                        $"血条锚点改用兜底高度 {defaultAnchorHeight} 米。" +
                        "注意：没有碰撞体的单位同时也永远索敌不到（见项目既有约定）。", entity);
                }

                return defaultAnchorHeight;
            }

            Bounds bounds = colliders[0].bounds;
            for (int i = 1; i < colliders.Length; i++)
            {
                bounds.Encapsulate(colliders[i].bounds);
            }

            float offset = bounds.max.y - entity.transform.position.y;

            // 量出来的高度不合理（单位根节点不在包围盒下方）时退回兜底值，避免血条贴在地面或飞到天上。
            return offset > 0.05f ? offset : defaultAnchorHeight;
        }

        /// <summary>按阵营刷新填充色（友方绿 / 敌方红 / 中立灰）。</summary>
        /// <param name="allyTeam">"我方"阵营。</param>
        private void ApplyFillColor(TeamType allyTeam)
        {
            if (fillImage == null || boundEntity == null)
            {
                return;
            }

            TeamType team = boundEntity.Team;

            if (team == TeamType.Neutral)
            {
                fillImage.color = neutralFillColor;
            }
            else
            {
                fillImage.color = team == allyTeam ? allyFillColor : enemyFillColor;
            }
        }

        /// <summary>血量变化：更新填充比例与显隐。由 HealthComponent.OnHealthChanged 驱动。</summary>
        private void HandleHealthChanged(float current, float max)
        {
            maxHealth = max > 0f ? max : 1f;
            healthPercent = Mathf.Clamp01(current / maxHealth);

            if (fillImage != null)
            {
                // Image.fillAmount 的 setter 内部已做相等性判断，不会因为"值没变"而重建网格。
                fillImage.fillAmount = healthPercent;
            }

            ApplyVisibility();
        }

        /// <summary>护盾变化：更新护盾覆盖条。护盾为 0 时把整条禁用（省掉一次无意义的绘制）。</summary>
        private void HandleShieldChanged(float shield)
        {
            if (shieldImage == null)
            {
                return;
            }

            float percent = Mathf.Clamp01(shield / maxHealth);
            shieldImage.fillAmount = percent;

            bool shouldShow = percent > 0f;
            if (shieldImage.enabled != shouldShow)
            {
                shieldImage.enabled = shouldShow;
            }
        }

        /// <summary>死亡：按配置隐藏血条。注意不清除绑定——管理器随后会通过注销事件回收本对象。</summary>
        private void HandleDied()
        {
            isDead = true;
            ApplyVisibility();
        }

        /// <summary>显隐判定的无参重载，供事件回调使用（事件回调里没有相机与锚点）。</summary>
        private void ApplyVisibility()
        {
            if (!isBound)
            {
                return;
            }

            ApplyVisibility(boundCamera, transform.position);
        }

        /// <summary>
        /// 计算并应用显隐。
        /// 三条规则：死亡隐藏、满血隐藏、相机背后隐藏。
        /// 视锥内的剔除交给渲染器（World Space Canvas 的几何参与正常视锥剔除），这里不重复做平面测试。
        /// </summary>
        /// <param name="cam">主相机。</param>
        /// <param name="anchor">血条当前的世界锚点。</param>
        private void ApplyVisibility(Camera cam, Vector3 anchor)
        {
            bool visible = isBound;

            if (visible && isDead && hideWhenDead)
            {
                visible = false;
            }

            // 0.999 而不是 1：血量是浮点累减的结果，用 == 1f 判断会漏掉"差一点点满血"的情况。
            if (visible && !isDead && hideWhenFull && healthPercent >= 0.999f)
            {
                visible = false;
            }

            if (visible && cam != null)
            {
                Vector3 toBar = anchor - cam.transform.position;

                // 相机背后的血条会被画成镜像（视锥剔除挡不住它），用一次点乘摘掉。
                if (Vector3.Dot(cam.transform.forward, toBar) <= 0f)
                {
                    visible = false;
                }
            }

            if (isVisible == visible)
            {
                return;
            }

            isVisible = visible;

            // 只禁用 Canvas 组件，绝不禁用本物体：本物体被禁用会触发 OnDisable → Unbind，
            // 血条会彻底脱离单位（而且不会再被管理器找到）。
            if (barCanvas != null && barCanvas.enabled != visible)
            {
                barCanvas.enabled = visible;
            }
        }
    }
}

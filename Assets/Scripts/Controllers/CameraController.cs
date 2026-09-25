using UnityEngine;

namespace MOBA.Controllers
{
    /// <summary>
    /// 相机控制器：主相机以固定的顶视角偏移平滑跟随一个目标（玩家英雄），并附带平移与缩放。
    ///
    /// 职责边界：
    /// 1. 跟随：把相机摆到「目标位置 + 偏移」处，并注视「目标位置 + 注视抬升」；
    /// 2. 平移：中键拖拽把画面整体推开（相机与注视点同步位移），中键双击回中；
    /// 3. 缩放：滚轮改变偏移向量的缩放系数（整体拉近/拉远，俯角保持不变）；
    /// 4. 不读写英雄的任何数据（只读它的 Transform.position），对战斗逻辑零侵入；
    /// 5. 偏移量固定在世界空间，【不】跟随目标旋转：英雄转身时画面跟着转会把顶视角操作毁掉，
    ///    这是顶视角相机与第三人称跟随相机的本质区别。
    ///
    /// 【为什么不把平移量/缩放写进 offset 字段】（阶段五验收项：平移与缩放不得改变跟随目标与跟随偏移）
    /// 平移量存在 panOffset、缩放系数存在 zoomScale，两者都只是「显示层」的增量：
    ///   · 期望位置 = 目标位置 + offset * zoomScale + panOffset
    ///   · 注视点   = 目标位置 + lookAtOffset + panOffset
    /// 注意注视点【必须】跟着 panOffset 一起平移：若注视点仍钉在英雄身上，相机就是一边横移一边继续盯着英雄，
    /// 表现是「英雄纹丝不动地停在屏幕中央」，那不叫平移而叫绕圈，玩家也永远推不开视野。
    ///
    /// 为什么位置写在 LateUpdate：
    /// 英雄的位移由 NavMeshAgent 在 Update 阶段推进（MovementComponent 不写 Transform，由引擎在 Update 内移动）。
    /// 相机若在 Update 里取位置，会在「引擎刚移动完、本帧渲染前」的时序上产生一帧抖动；
    /// LateUpdate 保证在所有 Update 之后取到本帧的最终位置，画面才不会抖。
    /// 输入则在 Update 里采集，两件事分开：一个只改「显示层增量」，一个只写 Transform。
    ///
    /// 输入方案说明：这里使用旧输入系统（Input.GetMouseButton / Input.mousePosition / Input.mouseScrollDelta）。
    /// 若项目在 Player Settings &gt; Active Input Handling 中只勾选了 "Input System Package (New)"，
    /// 这些 API 会抛 InvalidOperationException，此时需改为 "Both" 或把本类改写为基于 InputAction 的实现。
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraController : MonoBehaviour
    {
        [Header("跟随目标")]
        [Tooltip("跟随目标（玩家英雄）。留空时相机保持原位，可通过 SetTarget 在运行期指定。")]
        [SerializeField] private Transform target;

        [Header("视角偏移")]
        [Tooltip("相机相对目标的固定世界偏移。默认 (0, 10, -10) 是约 45 度的俯视角：\n" +
                 "能看清英雄周围的小范围交战，同时前方一段兵线也在画面内。")]
        [SerializeField] private Vector3 offset = new Vector3(0f, 10f, -10f);

        [Tooltip("注视点相对目标的抬升量。抬到英雄腰部而不是脚底，画面才不会把英雄压在屏幕正中偏上。")]
        [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1f, 0f);

        [Header("平滑")]
        [Tooltip("是否在 Start 时立刻吸附到目标位置（不做平滑）。\n" +
                 "默认开启：否则开局瞬间相机会从场景里摆放的位置一路飞向英雄，观感很差。")]
        [SerializeField] private bool snapOnStart = true;

        [Tooltip("平滑时间（秒）：越小越跟手，越大越有迟滞感。填 0 表示硬跟随（完全不做平滑）。")]
        [Min(0f)]
        [SerializeField] private float followSmoothTime = 0.15f;

        [Header("平移（中键拖拽）")]
        [Tooltip("是否启用中键拖拽平移。关闭后平移量保持为 0，视角完全锁在英雄身上。")]
        [SerializeField] private bool enablePan = true;

        [Tooltip("平移灵敏度（米/像素，缩放系数为 1 时）。1080p 下拖 300 像素约扫过 6 米。\n" +
                 "实际灵敏度会再乘以当前缩放系数——拉远时同样的像素位移必须扫过更大的地面范围，否则远视角下会「拖不动」。")]
        [Min(0f)]
        [SerializeField] private float panSensitivity = 0.02f;

        [Tooltip("平移量距离上限（米）。0 表示不限制。\n" +
                 "限制它的目的：既防止玩家把英雄彻底推出屏幕，也让「平移不改变跟随目标与跟随偏移」始终可验证。")]
        [Min(0f)]
        [SerializeField] private float maxPanDistance = 15f;

        [Tooltip("中键双击判定窗口（秒）：两次按下间隔小于该值视为「回中」操作。")]
        [Min(0f)]
        [SerializeField] private float recenterDoubleClickTime = 0.3f;

        [Header("缩放（滚轮）")]
        [Tooltip("是否启用滚轮缩放。")]
        [SerializeField] private bool enableZoom = true;

        [Tooltip("滚轮每格的缩放步进（缩放系数/格）。")]
        [Min(0f)]
        [SerializeField] private float zoomStep = 0.1f;

        [Tooltip("最近缩放系数（1 = 使用 offset 原值，0.5 = 偏移减半即拉近一倍）。")]
        [Min(0.01f)]
        [SerializeField] private float minZoomScale = 0.5f;

        [Tooltip("最远缩放系数（2 = 偏移翻倍即拉远一倍）。")]
        [Min(0.01f)]
        [SerializeField] private float maxZoomScale = 2f;

        [Header("调试")]
        [Tooltip("在 Console 输出一次「未指定跟随目标」的提示。")]
        [SerializeField] private bool logMissingTarget = true;

        /// <summary>SmoothDamp 需要的外部速度缓存，由引擎在调用间保持（必须复用同一个变量，否则平滑失效）。</summary>
        private Vector3 followVelocity;

        /// <summary>平移累计量（世界空间，y 恒为 0）。运行期状态，刻意不序列化：它只描述「当前镜头看向哪里」。</summary>
        private Vector3 panOffset;

        /// <summary>缩放系数（1 = 使用 offset 原值）。运行期状态，刻意不序列化。</summary>
        private float zoomScale = 1f;

        /// <summary>是否正处于中键拖拽中。</summary>
        private bool isPanning;

        /// <summary>上一帧的鼠标屏幕坐标（像素），用于计算拖拽增量。</summary>
        private Vector3 lastMousePosition;

        /// <summary>上一次按下中键的时刻（Time.unscaledTime），用于识别双击回中。</summary>
        private float lastMiddleClickTime = float.NegativeInfinity;

        /// <summary>是否已经提示过「没有跟随目标」，防止每帧刷日志。</summary>
        private bool hasReportedMissingTarget;

        /// <summary>当前跟随目标（只读）。</summary>
        public Transform Target => target;

        /// <summary>相机相对目标的固定世界偏移（只读），供调试视图复算期望位置。平移与缩放都不会修改它。</summary>
        public Vector3 Offset => offset;

        /// <summary>当前平移累计量（只读）。</summary>
        public Vector3 PanOffset => panOffset;

        /// <summary>当前缩放系数（只读）。</summary>
        public float ZoomScale => zoomScale;

        /// <summary>
        /// 生命周期入口：把缩放系数夹进 Inspector 配置的区间。
        /// 为什么需要：zoomScale 是运行期状态，初值 1 未必落在配置区间内（例如把 maxZoomScale 调成 0.5），
        /// 不夹住就会出现「一进游戏就是超出上限的视角」，而 OnValidate 只能约束序列化字段、管不到它。
        /// </summary>
        private void Awake()
        {
            float min = Mathf.Max(0.01f, minZoomScale);
            float max = Mathf.Max(min, maxZoomScale);
            zoomScale = Mathf.Clamp(1f, min, max);
        }

        /// <summary>
        /// 生命周期入口：按配置决定是否立刻吸附到目标。
        /// 不在这里做任何组件查找——本类只依赖一个 Transform 引用，没有别的依赖可解析。
        /// </summary>
        private void Start()
        {
            if (snapOnStart)
            {
                SnapToTarget();
            }
        }

        /// <summary>
        /// 每帧采集输入。只更新「显示层增量」（panOffset / zoomScale），不写 Transform——
        /// 写 Transform 统一放在 LateUpdate，保证同一帧内的位移只算一次。
        /// </summary>
        private void Update()
        {
            HandlePanInput();
            HandleZoomInput();
        }

        /// <summary>
        /// 每帧跟随。放在 LateUpdate 的原因见类注释。
        /// </summary>
        private void LateUpdate()
        {
            if (target == null)
            {
                // 目标可能被销毁（英雄死亡后销毁、换场景），此时相机保持原位即可，只提示一次。
                ReportMissingTargetOnce();
                return;
            }

            Vector3 desiredPosition = ComputeDesiredPosition();

            if (followSmoothTime <= 0f)
            {
                // 硬跟随：直接赋值，同时清掉速度缓存，避免下次切回平滑时带着旧速度冲一下。
                followVelocity = Vector3.zero;
                transform.position = desiredPosition;
            }
            else
            {
                // SmoothDamp 内部按时间常数收敛，与帧率无关；传入 ref 缓存速度以保证跨帧连续。
                transform.position = Vector3.SmoothDamp(
                    transform.position, desiredPosition, ref followVelocity, followSmoothTime);
            }

            // 镜头始终注视「目标 + 注视抬升 + 平移量」。因为偏移是世界空间的固定值，
            // 不缩放不平移时这个朝向实际上恒定不变，但仍然每帧写一次——
            // 否则目标被替换或平移量变化后，朝向会残留旧值。
            transform.LookAt(ComputeFocusPoint());
        }

        /// <summary>计算相机应处的世界位置。平移与缩放都在这里生效，offset 字段本身保持不变。</summary>
        private Vector3 ComputeDesiredPosition()
        {
            return target.position + offset * zoomScale + panOffset;
        }

        /// <summary>
        /// 计算注视点。平移量必须同时作用在注视点上，理由见类注释（否则平移退化为绕英雄转圈）。
        /// </summary>
        private Vector3 ComputeFocusPoint()
        {
            return target.position + lookAtOffset + panOffset;
        }

        /// <summary>
        /// 采集中键拖拽：按下 → 记录起点；按住 → 按鼠标像素增量推动画面；双击 → 回中。
        /// </summary>
        private void HandlePanInput()
        {
            if (!enablePan)
            {
                // 关闭平移时若仍处于拖拽状态必须复位，否则再次开启会带着一段「幽灵拖拽」的增量。
                isPanning = false;
                return;
            }

            if (Input.GetMouseButtonDown(2))
            {
                float now = Time.unscaledTime;

                // 中键双击 = 回到英雄（平移量清零）。这是唯一「复位视角」的入口：
                // 没有它，玩家把视野推远之后就再也找不回英雄了（平移量是持续累加的）。
                if (now - lastMiddleClickTime <= Mathf.Max(0f, recenterDoubleClickTime))
                {
                    Recenter();
                }

                lastMiddleClickTime = now;
                isPanning = true;
                lastMousePosition = Input.mousePosition;
                return;
            }

            if (Input.GetMouseButtonUp(2))
            {
                isPanning = false;
                return;
            }

            if (!isPanning || !Input.GetMouseButton(2))
            {
                return;
            }

            Vector3 currentMousePosition = Input.mousePosition;
            Vector3 mouseDelta = currentMousePosition - lastMousePosition;
            lastMousePosition = currentMousePosition;

            if (mouseDelta.sqrMagnitude <= 0f)
            {
                return;
            }

            ApplyPanDelta(mouseDelta);
        }

        /// <summary>
        /// 把一次鼠标像素增量转换为世界平移量并累加（带距离上限）。
        /// 用像素增量而不是 Input.GetAxis("Mouse X")：后者的返回值会被 Input Manager 的灵敏度设置缩放，
        /// 同一个配置在不同项目里手感不一致；像素增量与 panSensitivity（米/像素）一一对应，可解释、可复算。
        /// </summary>
        private void ApplyPanDelta(Vector3 mouseDelta)
        {
            // 把相机的右/前方向压到水平面上：俯视相机的 forward 是斜向下的，
            // 不做投影的话「往前推」会带着相机一起往下窜，视野忽高忽低。
            Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

            // 拖拽语义 = 「抓住地面拖动」：鼠标往右，画面内容跟着往右，因此相机要往左走（取负号）。
            float metersPerPixel = Mathf.Max(0f, panSensitivity) * zoomScale;
            Vector3 move = -(right * mouseDelta.x + forward * mouseDelta.y) * metersPerPixel;

            // 平移只发生在水平面：让相机高度完全由 offset.y * zoomScale 决定，俯角因此恒定。
            move.y = 0f;

            panOffset += move;

            float maxDistance = Mathf.Max(0f, maxPanDistance);
            if (maxDistance > 0f && panOffset.sqrMagnitude > maxDistance * maxDistance)
            {
                panOffset = panOffset.normalized * maxDistance;
            }
        }

        /// <summary>
        /// 采集滚轮缩放：只改缩放系数，不改 offset。
        /// 缩放系数作用于整个偏移向量（含高度），因此俯角保持不变，视觉上就是沿视线方向拉近/推远。
        /// </summary>
        private void HandleZoomInput()
        {
            if (!enableZoom)
            {
                return;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(scroll, 0f))
            {
                return;
            }

            float min = Mathf.Max(0.01f, minZoomScale);
            float max = Mathf.Max(min, maxZoomScale);

            // 滚轮向上（正值）= 拉近 = 偏移向量变短，因此取负号。
            zoomScale = Mathf.Clamp(zoomScale - scroll * Mathf.Max(0f, zoomStep), min, max);
        }

        /// <summary>
        /// 设置跟随目标。典型用途：对局开始时英雄才被实例化（一键组装工具也是走这条路径）。
        /// 赋值后不会立刻吸附，需要立即就位请紧接着调用 <see cref="SnapToTarget"/>。
        /// 刻意不清空平移量与缩放：换人不等于重置玩家当前视角。
        /// </summary>
        /// <param name="newTarget">新的跟随目标，允许传 null 表示停止跟随。</param>
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            hasReportedMissingTarget = false;
        }

        /// <summary>
        /// 立刻把相机吸附到目标位置与朝向（不做平滑）。
        /// 编辑器工具在组装场景时也会调用本方法，让 Scene 视图当场就呈现出正确的视角
        /// ——本方法只写 Transform，在编辑模式下调用是安全的。
        /// </summary>
        public void SnapToTarget()
        {
            if (target == null)
            {
                return;
            }

            transform.position = ComputeDesiredPosition();
            transform.LookAt(ComputeFocusPoint());

            // 清掉速度缓存：吸附是瞬时的，残留速度会让下一次平滑跟随出现反向拉扯。
            followVelocity = Vector3.zero;
        }

        /// <summary>把平移量清零，视角重新回到英雄身上（中键双击走的就是这里）。缩放系数不受影响。</summary>
        public void Recenter()
        {
            panOffset = Vector3.zero;
        }

        /// <summary>把平移量与缩放系数一并复位到初始视角，供编辑器工具与自动化测试调用。</summary>
        public void ResetView()
        {
            Recenter();

            float min = Mathf.Max(0.01f, minZoomScale);
            float max = Mathf.Max(min, maxZoomScale);
            zoomScale = Mathf.Clamp(1f, min, max);
        }

        /// <summary>
        /// 编辑器内校验：把互相矛盾的配置就地纠正。
        /// 特别是 minZoomScale &gt; maxZoomScale：此时 Mathf.Clamp 的返回值是未定义的，
        /// 表现为「滚轮怎么滚都不动」且毫无报错，必须在这里拦下。
        /// </summary>
        private void OnValidate()
        {
            minZoomScale = Mathf.Max(0.01f, minZoomScale);
            maxZoomScale = Mathf.Max(minZoomScale, maxZoomScale);
            zoomScale = Mathf.Clamp(zoomScale, minZoomScale, maxZoomScale);

            panSensitivity = Mathf.Max(0f, panSensitivity);
            maxPanDistance = Mathf.Max(0f, maxPanDistance);
            recenterDoubleClickTime = Mathf.Max(0f, recenterDoubleClickTime);
        }

        /// <summary>报告一次「没有跟随目标」。用一次性标记防刷屏。</summary>
        private void ReportMissingTargetOnce()
        {
            if (hasReportedMissingTarget)
            {
                return;
            }

            hasReportedMissingTarget = true;

            if (logMissingTarget)
            {
                Debug.LogWarning(
                    $"[CameraController] {name} 未指定跟随目标，相机将保持当前位置不动。" +
                    "请指定玩家英雄，或在运行期调用 SetTarget。", this);
            }
        }
    }
}

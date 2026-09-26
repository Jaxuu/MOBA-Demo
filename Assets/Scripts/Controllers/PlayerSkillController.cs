using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.Controllers
{
    /// <summary>
    /// 玩家技能输入控制器：把「Q / W / E / R 按键 + 鼠标位置」翻译成「SkillComponent.TryCast 调用」。
    ///
    /// 【为什么独立于 PlayerCommandController 而不合并】
    /// 1. 职责不同：PlayerCommandController 处理的是"右键 = 移动或普攻"这条单一指令链，
    ///    内部维护着"追击进度 / 已下令标记 / 已停下输出标记"一整套状态；技能是另一条完全独立的输入通道；
    /// 2. 合进去会让那个类变成"输入上帝类"（它已经有 700+ 行），且两套状态互相干扰的概率很高
    ///    （例如"施法前摇被移动指令撤销"这种 bug 会变得很难定位）。
    /// 因此本项目把输入层拆成两个互不引用的控制器，各管一条通道。
    ///
    /// 职责边界：
    /// 1. 只做"采集按键 → 射线拾取目标/落点 → 交给 SkillComponent"；
    /// 2. 不校验冷却 / 蓝量 / 距离 / 目标合法性——那是 SkillComponent 校验链的唯一职责，
    ///    在这里重写一遍必然导致两处规则分叉；
    /// 3. 不生成弹道、不结算伤害、不播表现。
    ///
    /// 【V1 采用智能施法（按下即释放）】按 Q/W/E/R 立刻以鼠标当前位置为目标释放，无需二次确认。
    /// "按 Q 进入瞄准 → 左键确认 → 右键取消"的交互需要技能指示器 UI（方向箭头 / 范围圈），
    /// 属阶段七 HUD 的范围。
    ///
    /// 【阶段八：按键扩到四槽，并按技能配置决定拾取哪一侧】
    /// 拾取规则完全由 SkillData 驱动，输入层不做任何"这个技能大概想要什么"的猜测：
    ///   · CastType = UnitTarget + TargetsAlly = false → 只拾取敌方单位（鼠标下优先，其次已锁定的普攻目标）；
    ///   · CastType = UnitTarget + TargetsAlly = true  → 只拾取友方单位；
    ///   · CastType = GroundPoint                      → 拾取地面落点（射线未命中地面时退回地面平面交点）；
    ///   · CastType = Self                             → 不需要目标与落点，落点直接取自身位置。
    /// 一次射线扫描同时记录"最近敌方""最近友方""最近地面"三个候选，最后按配置挑一个——
    /// 三条拾取路径的口径（距离计算、Layer 过滤、自己排除）因此保持完全一致。
    ///
    /// 输入方案说明：与 PlayerCommandController 一致，使用旧输入系统 Input.GetKeyDown。
    /// 若 Player Settings &gt; Active Input Handling 只勾选了 "Input System Package (New)"，
    /// 该 API 会抛 InvalidOperationException，需改为 "Both" 或改写为 InputAction 实现。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerSkillController : MonoBehaviour
    {
        [Header("受控单位")]
        [Tooltip("当前被玩家操控的实体（玩家英雄）。留空时自动取同一 GameObject 上的 EntityBase。")]
        [SerializeField] private EntityBase controlledEntity;

        [Tooltip("技能组件。留空时自动取同一 GameObject 上的 SkillComponent。")]
        [SerializeField] private SkillComponent skillComponent;

        [Header("拾取")]
        [Tooltip("地面所在的 Layer 名称，需与 Edit > ProjectSettings > Tags and Layers 中的命名完全一致。")]
        [SerializeField] private string groundLayerName = "Ground";

        [Tooltip("参与拾取的 Layer。必须包含单位所在的 Layer 与地面 Layer，否则对应的拾取会失败。")]
        [SerializeField] private LayerMask commandLayers = Physics.DefaultRaycastLayers;

        [Tooltip("射线最大检测距离；过小会导致远端点不到地面。")]
        [SerializeField] private float maxRayDistance = 500f;

        [Tooltip("地面兜底平面的高度（米）。鼠标没有命中任何地面碰撞体时，用射线与该平面的交点作为落点。")]
        [SerializeField] private float groundPlaneY = 0f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次技能指令的拾取结果与施法成败（被拒绝时会带上具体原因，如冷却 / 法力 / 距离 / 目标非法）。")]
        [SerializeField] private bool logCastCommands = true;

        [Tooltip("在 Scene 视图中绘制技能拾取射线。")]
        [SerializeField] private bool drawDebugRay = false;

        /// <summary>射线拾取缓冲区。预分配复用，点击不产生 GC Alloc（与 PlayerCommandController 同一做法）。</summary>
        private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        /// <summary>缓存的主相机。Camera.main 内部是带标签的查找，不该放在每帧调用。</summary>
        private Camera mainCamera;

        /// <summary>地面 Layer 索引。避免每次拾取都调 LayerMask.NameToLayer（会做字符串解析）。</summary>
        private int groundLayerIndex;

        /// <summary>初始化是否成功。任一必需依赖缺失都会置为 false，Update 直接短路。</summary>
        private bool isInitialized;

        /// <summary>是否已就"缺少 SkillComponent"告警过。</summary>
        private bool hasWarnedMissingSkillComponent;

        // 说明：这里刻意【不】保留"缺少地面 Layer 的一次性告警标记"。
        // 该告警发生在 Awake 里，而 Awake 每个组件实例只执行一次，标记永远没有第二个消费方 ——
        // 它会被编译器报 CS0414（赋值后从未读取），并让读者误以为"存在重复告警需要去重"。

        /// <summary>是否已就"缺少主相机"报过错误。</summary>
        private bool hasReportedMissingCamera;

        /// <summary>当前受控实体（只读）。</summary>
        public EntityBase ControlledEntity => controlledEntity;

        /// <summary>
        /// 技能组件（延迟解析）。
        /// 为什么不在 Awake 里固定：用代码动态创建英雄时，组件挂载顺序不可控，
        /// Awake 缓存到的可能是 null 且永远不会更新（与 CombatComponent.Owner 同一思路）。
        /// </summary>
        private SkillComponent ResolvedSkillComponent
        {
            get
            {
                if (skillComponent == null)
                {
                    skillComponent = GetComponent<SkillComponent>();
                }

                return skillComponent;
            }
        }

        /// <summary>
        /// 缓存相机、解析地面 Layer，并做一次性依赖校验。
        /// 与 PlayerCommandController 同构：把配置错误集中在初始化阶段报出，而不是每帧报错。
        /// </summary>
        private void Awake()
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                Debug.LogError(
                    "[PlayerSkillController] 场景中找不到 MainCamera（请确认相机带有 MainCamera 标签）。技能输入已禁用。", this);
                enabled = false;
                return;
            }

            // NameToLayer 在 Layer 不存在时返回 -1，必须显式判断，否则后续判断会静默失效。
            int groundLayer = LayerMask.NameToLayer(groundLayerName);
            if (groundLayer < 0)
            {
                // 这里刻意只告警不禁用：非指向性技能（W）需要地面落点，但指向性技能（Q）只依赖单位拾取，
                // 禁用整个控制器会让"地面 Layer 没配"这一个问题连带把 Q 也废掉。
                Debug.LogWarning(
                    $"[PlayerSkillController] 未找到名为 \"{groundLayerName}\" 的 Layer，" +
                    "非指向性技能的落点将退回「射线与地面平面的交点」计算。请到 ProjectSettings > Tags and Layers 中确认该 Layer 存在。", this);
            }

            groundLayerIndex = groundLayer;

            // 同物体自解析：本控制器既可挂在英雄身上（一键组装工具的默认做法），
            // 也可挂在场景级管理器上，通过 Inspector 指定受控单位。
            if (controlledEntity == null)
            {
                controlledEntity = GetComponent<EntityBase>();
            }

            if (controlledEntity == null)
            {
                Debug.LogWarning("[PlayerSkillController] 尚未指定受控实体，在赋值前不会响应任何技能按键。", this);
            }

            isInitialized = true;
        }

        /// <summary>
        /// 每帧采集技能按键。用 GetKeyDown 而不是 GetKey：
        /// 技能是"按一下放一次"的动作，长按连发属于连招/蓄力机制，README 明确排除。
        ///
        /// 【阶段八：槽位从 Q / W 扩到 Q / W / E / R】四键各自独立采集，逐个交给同一条处理链；
        /// 不写成循环是为了让"哪个键对应哪个槽位"在代码里一眼可见（按键映射属于交互决策，
        /// 用一张表反而要多跳一层才能确认）。
        /// </summary>
        private void Update()
        {
            if (!isInitialized)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Q))
            {
                HandleSkillKey(SkillSlot.Q);
            }

            if (Input.GetKeyDown(KeyCode.W))
            {
                HandleSkillKey(SkillSlot.W);
            }

            if (Input.GetKeyDown(KeyCode.E))
            {
                HandleSkillKey(SkillSlot.E);
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                HandleSkillKey(SkillSlot.R);
            }
        }

        /// <summary>
        /// 处理一次技能按键：拾取目标 → 交给 SkillComponent 校验并施法。
        /// </summary>
        /// <param name="slot">被按下的技能槽位。</param>
        private void HandleSkillKey(SkillSlot slot)
        {
            if (controlledEntity == null)
            {
                return;
            }

            // 已死亡的受控单位不再响应任何指令（与 PlayerCommandController 同一道闸门）。
            // 注意这不是重复校验：SkillComponent 内部也有一道，这里是"输入层的第一道"，
            // 作用是不让已死亡的英雄继续消耗输入事件与拾取开销。
            HealthComponent controlledHealth = controlledEntity.Health;
            if (controlledHealth != null && controlledHealth.IsDead)
            {
                return;
            }

            SkillComponent skills = ResolvedSkillComponent;
            if (skills == null)
            {
                ReportMissingSkillComponentOnce();
                return;
            }

            // 先取配置：需要知道施法类型才能决定"这次该拾取敌方单位还是地面落点"。
            // 未配置时直接返回——SkillComponent 内部会就"该槽位未配置"告警一次，这里不重复。
            SkillData data = skills.GetSkillData(slot);
            if (data == null)
            {
                return;
            }

            if (mainCamera == null)
            {
                if (!hasReportedMissingCamera)
                {
                    hasReportedMissingCamera = true;
                    Debug.LogError("[PlayerSkillController] 主相机引用已失效（可能被销毁），技能输入已禁用。", this);
                }

                enabled = false;
                return;
            }

            Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

            if (drawDebugRay)
            {
                Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, Color.magenta, 0.5f);
            }

            // 与 PlayerCommandController.HandleRightClick 同一套拾取逻辑：
            // 在全部命中里分别找"最近的敌方单位""最近的友方单位"与"最近的地面落点"。
            //
            // 【阶段八：为什么要同时找敌我两侧】技能的目标阵营由 SkillData.TargetsAlly 决定
            // （治疗/护盾类只认友方，伤害/控制类只认敌方）。一次射线只扫一遍，
            // 两侧各留一个"最近候选"，最后按技能配置挑一个 —— 比"先看技能类型再决定拾取哪一侧"
            // 少一次分支，也让两侧的拾取口径（距离计算、Layer 过滤）保持完全一致。
            EntityBase hoveredEnemy = null;
            float nearestEnemySqrDistance = float.MaxValue;
            EntityBase hoveredAlly = null;
            float nearestAllySqrDistance = float.MaxValue;
            bool hasGroundPoint = false;
            Vector3 groundPoint = Vector3.zero;
            float nearestGroundSqrDistance = float.MaxValue;

            int hitCount = Physics.RaycastNonAlloc(
                ray, hitBuffer, maxRayDistance, commandLayers, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = hitBuffer[i];
                Collider hitCollider = hit.collider;

                if (hitCollider == null)
                {
                    continue;
                }

                EntityBase hitEntity = hitCollider.GetComponentInParent<EntityBase>();

                if (hitEntity != null)
                {
                    // 点到自己：忽略。Self 技能本来就不需要目标，而指向性技能不该把"自己"当成候选
                    // （否则按 Q 指自己脚下会得到一个"目标非法"的拒绝日志，噪声大于信息量）。
                    if (hitEntity == controlledEntity)
                    {
                        continue;
                    }

                    float entitySqrDistance = (hit.point - ray.origin).sqrMagnitude;

                    if (IsEnemy(hitEntity))
                    {
                        if (entitySqrDistance < nearestEnemySqrDistance)
                        {
                            nearestEnemySqrDistance = entitySqrDistance;
                            hoveredEnemy = hitEntity;
                        }
                    }
                    else if (hitEntity.Team != TeamType.Neutral)
                    {
                        // 友方候选（中立单位两边都不算，与 Targetable 的敌我规则逐条对齐）。
                        if (entitySqrDistance < nearestAllySqrDistance)
                        {
                            nearestAllySqrDistance = entitySqrDistance;
                            hoveredAlly = hitEntity;
                        }
                    }

                    // 单位碰撞体不作为地面落点，继续扫描。
                    continue;
                }

                if (hitCollider.gameObject.layer != groundLayerIndex)
                {
                    continue;
                }

                float groundSqrDistance = (hit.point - ray.origin).sqrMagnitude;
                if (groundSqrDistance < nearestGroundSqrDistance)
                {
                    nearestGroundSqrDistance = groundSqrDistance;
                    groundPoint = hit.point;
                    hasGroundPoint = true;
                }
            }

            // 地面兜底：鼠标指向天空盒 / 没有任何地面碰撞体时，用射线与地面平面的交点作为落点。
            // 必要性：非指向性技能"点哪儿放哪儿"，若因为鼠标刚好滑出地面碰撞体就静默失败，
            // 手感上表现为"W 时灵时不灵"，而玩家完全无从判断原因。
            if (!hasGroundPoint)
            {
                hasGroundPoint = TryIntersectGroundPlane(ray, out groundPoint);
            }

            ITargetable target = null;

            switch (data.CastType)
            {
                case SkillCastType.UnitTarget:
                    // 目标优先级：鼠标下的对应阵营单位 → 当前已锁定的普攻目标（仅敌方技能）→ 无。
                    // 为什么保留第二档：玩家先右键锁定敌人、再按 Q 甩技能是 MOBA 里最常见的操作习惯；
                    // 而且 CurrentTarget 是全项目"当前目标"的唯一存放处，读取它不引入第二份数据。
                    // 友方技能不读它 —— 那是攻击目标，把它当治疗目标在语义上就是错的。
                    if (data.TargetsAlly)
                    {
                        target = hoveredAlly != null ? (ITargetable)hoveredAlly : null;
                    }
                    else
                    {
                        target = hoveredEnemy != null ? (ITargetable)hoveredEnemy : ResolveLockedTarget();
                    }

                    break;

                case SkillCastType.Self:
                    // 自身施法不需要目标，也不需要落点：把落点填成自身位置，
                    // 让 SkillComponent 的日志与"以自身为中心的范围场"有同一个锚点。
                    target = null;
                    groundPoint = controlledEntity.transform.position;
                    break;
            }

            bool casted = skills.TryCast(slot, groundPoint, target, out string failReason);

            if (logCastCommands)
            {
                string targetName = target is Component targetComponent ? targetComponent.name : "无目标";
                string resultText = casted ? "已进入施法" : $"被拒绝 —— {failReason}";

                // 【为什么把失败原因一起打出来】只打"被拒绝"的话，使用者看到的是"按了没反应"，
                // 完全分不清是冷却、没蓝、距离不够还是目标选错了——而这四者的处理方式完全不同。
                // SkillComponent.TryCast 的 failReason 出参已经把结论写成人话（含剩余冷却秒数、
                // 所需与当前法力、实际距离与射程），这里直接透传，不做二次加工。
                //
                // 用 Debug.Log 而不是 LogWarning：技能被拒绝是玩家的正常操作结果（CD 没好、蓝不够…），
                // 不是配置错误。打成 Warning 会让"连按两下 Q"就刷出一片黄字，把真正的配置告警淹掉。
                Debug.Log(
                    $"[PlayerSkillController] {slot} 技能指令 → {controlledEntity.name}" +
                    $"｜落点 {groundPoint.ToString("F2")}｜目标 {targetName}｜结果 {resultText}", this);
            }
        }

        /// <summary>
        /// 取当前已锁定的普攻目标。数据来源是 TargetingComponent.CurrentTarget（全项目唯一存放处）。
        /// </summary>
        private ITargetable ResolveLockedTarget()
        {
            TargetingComponent targeting = controlledEntity.Targeting;
            return targeting != null ? targeting.CurrentTarget : null;
        }

        /// <summary>
        /// 求鼠标射线与地面平面（y = groundPlaneY）的交点。
        /// 只在"射线没有命中任何地面碰撞体"时作为兜底使用。
        /// </summary>
        private bool TryIntersectGroundPlane(Ray ray, out Vector3 point)
        {
            point = Vector3.zero;

            Plane groundPlane = new Plane(Vector3.up, new Vector3(0f, groundPlaneY, 0f));
            if (!groundPlane.Raycast(ray, out float distance))
            {
                // 射线与平面平行（例如相机几乎贴着地面平视）：无法求交点。
                return false;
            }

            point = ray.GetPoint(distance);
            return true;
        }

        /// <summary>
        /// 敌我校验。优先复用 TargetingComponent 的规则，保证"能锁定"与"能放技能"用的是同一套判断；
        /// 退化路径与 CombatComponent / PlayerCommandController 逐条对齐（含中立阵营排除）。
        /// </summary>
        private bool IsEnemy(EntityBase candidate)
        {
            if (candidate == null || controlledEntity == null)
            {
                return false;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            if (targeting != null)
            {
                return targeting.IsEnemy(candidate);
            }

            if (candidate.Team == TeamType.Neutral)
            {
                return false;
            }

            return candidate.Team != controlledEntity.Team;
        }

        /// <summary>
        /// 运行时切换受控单位（对局开始时英雄才被实例化、或后续做英雄切换时使用）。
        /// </summary>
        /// <param name="entity">新的受控实体，允许传 null 表示解除控制。</param>
        public void SetControlledEntity(EntityBase entity)
        {
            controlledEntity = entity;

            // 换人等于丢弃旧的组件缓存：不清会让新单位误用旧单位的技能组件。
            skillComponent = null;
            hasWarnedMissingSkillComponent = false;
        }

        /// <summary>就"受控实体缺少 SkillComponent"告警一次。</summary>
        private void ReportMissingSkillComponentOnce()
        {
            if (hasWarnedMissingSkillComponent)
            {
                return;
            }

            hasWarnedMissingSkillComponent = true;
            Debug.LogWarning(
                $"[PlayerSkillController] 受控实体 {controlledEntity.name} 没有 SkillComponent，无法释放任何技能。" +
                "请为一键组装工具的英雄预制体补齐该组件。", this);
        }
    }
}

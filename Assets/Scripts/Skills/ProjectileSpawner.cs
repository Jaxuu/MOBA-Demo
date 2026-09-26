using System;
using UnityEngine;
using MOBA.Core;

namespace MOBA.Skills
{
    /// <summary>
    /// 弹道生成器：全项目【唯一】持有弹道外观预制体的组件。
    ///
    /// 【为什么需要这一层】README §3.4 要求逻辑层禁止引用表现资源。若让 SkillComponent 直接持有
    /// 弹道预制体，它（逻辑层）就间接持有了表现资源，等于绕开铁律；而弹道本身又确实需要"外观模板"。
    /// 折中方案是把"外观"这件事收敛到一个专门的生成器里：SkillComponent 只调 Spawn(...)，
    /// 不知道弹道长什么样、是预制体还是代码生成的。
    ///
    /// 额外收益：阶段八把 Instantiate 换成对象池时，只改本类内部，技能逻辑与预制体资产都不受影响。
    ///
    /// 【为什么挂在施法者身上而不是做成单例】施法者可能不止一个（英雄、将来的塔、投掷型小兵），
    /// 每个施法者各自持有自己的一套弹道配置更自然；且项目既有约定就是"能力组件挂在使用者身上"。
    /// </summary>
    [DisallowMultipleComponent]
    public class ProjectileSpawner : MonoBehaviour
    {
        [Header("弹道外观（由 AutoSceneBuilder 注入；缺失时代码兜底生成白盒球体）")]
        [Tooltip("弹道预制体。必须挂有 Projectile 组件，且【不要】带 Collider——弹道走自研距离判定，不参与物理查询。")]
        [SerializeField] private GameObject projectilePrefab;

        [Header("兜底")]
        [Tooltip("预制体缺失时是否用代码生成一个白盒球体。开启可保证「技能一定能飞出来」，代价是外观不可控。")]
        [SerializeField] private bool createFallbackPrimitiveWhenPrefabMissing = true;

        [Header("发射")]
        [Tooltip("发射点相对施法者原点的高度（米）。抬到胸口高度，弹道看起来才是「从身上飞出去」而不是「从脚底冒出来」。")]
        [SerializeField] private float launchHeight = 1f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次弹道生成记录。")]
        [SerializeField] private bool logSpawnEvents = false;

        /// <summary>是否已就"预制体缺失"告警过。</summary>
        private bool hasWarnedMissingPrefab;

        /// <summary>兜底材质缓存。只创建一次并复用，避免每发弹道克隆一份材质实例（材质泄漏）。</summary>
        private Material fallbackMaterial;

        /// <summary>当前配置的弹道预制体（只读），供编辑器工具做收尾校验。</summary>
        public GameObject ProjectilePrefab => projectilePrefab;

        /// <summary>
        /// 弹道生成事件（阶段九新增）：每生成一发弹道（技能弹道与普攻弹道都算）广播一次，
        /// 参数是刚生成并已完成 Initialize 的 <see cref="Projectile"/>。
        ///
        /// 【为什么表现层需要它】弹道是**运行期动态创建**的短命对象（命中即 Destroy），
        /// 既不在 <c>EntityRegistry</c> 里，也没有任何"注册表"能枚举到它。
        /// 想订阅 <c>Projectile.OnHit</c> 播放命中特效，唯一的接入点就是"它被生成的那一刻"。
        ///
        /// 【层级说明】本事件不违反「逻辑层不引用表现资源」：它只是把"我生成了一个弹道"
        /// 这一既有事实对外广播，订阅方（VfxSpawner）自取所需。逻辑层不知道有谁在听，
        /// 也没有因此多持有任何特效 / 音效引用。
        ///
        /// 【订阅方注意】弹道命中或超时后会自行 Destroy 并把 OnHit 置空，
        /// 因此订阅方**不需要**退订单个弹道（委托链随对象一起消失）。
        /// </summary>
        public event Action<Projectile> OnProjectileSpawned;

        /// <summary>
        /// 生成一发弹道。
        /// </summary>
        /// <param name="source">施法者（弹道起点与伤害归属）。</param>
        /// <param name="target">飞行目标（允许已失效，弹道会安全飞完并回收）。</param>
        /// <param name="data">技能配置。</param>
        /// <returns>生成的弹道组件；生成失败返回 null（调用方据此决定是否广播释放事件）。</returns>
        public Projectile Spawn(EntityBase source, ITargetable target, SkillData data)
        {
            if (source == null || data == null)
            {
                return null;
            }

            Projectile projectile = CreateInitializedProjectile(source, target, data.ProjectileRadius);
            if (projectile == null)
            {
                return null;
            }

            projectile.Initialize(source, target, data);

            if (logSpawnEvents)
            {
                string targetName = target is Component component ? component.name : "null";
                Debug.Log(
                    $"[ProjectileSpawner] {name} 生成弹道 {data.DisplayName}（速度 {data.ProjectileSpeed:F1}，" +
                    $"命中半径 {data.ProjectileRadius:F2}）→ 目标 {targetName}", this);
            }

            // 广播"弹道已就位"（阶段九）：必须在 Initialize 之后 ——
            // 订阅方会立刻挂 OnHit，而 Initialize 之前的弹道还没有飞行参数、可能当帧就被回收。
            OnProjectileSpawned?.Invoke(projectile);

            return projectile;
        }

        /// <summary>
        /// 生成一发「普攻弹道」（阶段八新增）：由防御塔等固定建筑调用。
        ///
        /// 与技能弹道的唯一区别是载荷——普攻的数值来源是 AttackData，与 SkillData 无关，
        /// 因此这里直接收数值而不是收一份技能配置。飞行与命中逻辑完全共用。
        /// </summary>
        /// <param name="source">发射者（塔）。</param>
        /// <param name="target">飞行目标。</param>
        /// <param name="damage">命中时结算的伤害值。</param>
        /// <param name="speed">飞行速度（米/秒）。</param>
        /// <param name="radius">命中半径（米）。</param>
        /// <param name="maxTravelDistance">最大飞行距离（米）。</param>
        /// <returns>生成的弹道组件；生成失败返回 null（调用方需据此走降级分支）。</returns>
        public Projectile SpawnAttack(
            EntityBase source,
            ITargetable target,
            float damage,
            float speed,
            float radius,
            float maxTravelDistance)
        {
            if (source == null)
            {
                return null;
            }

            Projectile projectile = CreateInitializedProjectile(source, target, radius);
            if (projectile == null)
            {
                return null;
            }

            projectile.InitializeAttack(source, target, damage, speed, radius, maxTravelDistance);

            if (logSpawnEvents)
            {
                string targetName = target is Component component ? component.name : "null";
                Debug.Log(
                    $"[ProjectileSpawner] {name} 生成普攻弹道（伤害 {damage:F1}，速度 {speed:F1}）→ 目标 {targetName}", this);
            }

            OnProjectileSpawned?.Invoke(projectile);

            return projectile;
        }

        /// <summary>
        /// 创建弹道实例、摆到发射点并取出 Projectile 组件（技能弹道与普攻弹道共用的前半段）。
        /// 把这一段抽出来的理由：位置与组件校验是两条路径都必须一致的一步，
        /// 各写一份迟早出现"技能弹道从胸口发射、普攻弹道从脚底发射"这类口径差异。
        /// </summary>
        /// <param name="source">发射者（提供发射点）。</param>
        /// <param name="target">飞行目标（仅用于日志，允许为 null）。</param>
        /// <param name="hitRadius">命中半径（米），兜底白盒球体按它换算视觉尺寸。</param>
        /// <returns>可用的弹道组件；创建或校验失败返回 null。</returns>
        private Projectile CreateInitializedProjectile(EntityBase source, ITargetable target, float hitRadius)
        {
            GameObject instance = CreateProjectileInstance(hitRadius);
            if (instance == null)
            {
                return null;
            }

            instance.transform.position = source.transform.position + Vector3.up * launchHeight;

            Projectile projectile = instance.GetComponent<Projectile>();
            if (projectile == null)
            {
                // 预制体被配错了（忘了挂 Projectile）。必须报错：否则弹道会静止在空中且永不结算，
                // 而 Console 里没有任何线索。
                Debug.LogError(
                    $"[ProjectileSpawner] 弹道对象 {instance.name} 上没有 Projectile 组件，本次弹道已取消。" +
                    "请检查弹道预制体（一键组装工具生成的 Projectile.prefab 自带该组件）。", this);
                Destroy(instance);
                return null;
            }

            return projectile;
        }

        /// <summary>
        /// 创建一个弹道实例：优先用预制体，缺失时按开关决定是否代码兜底。
        /// </summary>
        /// <param name="hitRadius">命中半径（米），仅用于兜底球体的视觉缩放。</param>
        private GameObject CreateProjectileInstance(float hitRadius)
        {
            if (projectilePrefab != null)
            {
                return Instantiate(projectilePrefab);
            }

            WarnMissingPrefabOnce();

            if (!createFallbackPrimitiveWhenPrefabMissing)
            {
                return null;
            }

            GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            fallback.name = "Projectile_Fallback";

            // 【必须去掉兜底球体的碰撞体】CreatePrimitive 会附带一个 SphereCollider，而本项目的
            // 索敌（Physics.OverlapSphere）与右键拾取（Physics.RaycastNonAlloc）都会命中它——
            // 弹道会变成一个"能被右键点到的候选单位"，并在索敌结果里占用候选位。
            // 禁用 + 销毁双保险：Destroy 延迟到帧末执行，中间这一帧里 disabled 的碰撞体不参与任何物理查询。
            SphereCollider collider = fallback.GetComponent<SphereCollider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }

            float diameter = Mathf.Max(0.1f, hitRadius * 2f);
            fallback.transform.localScale = new Vector3(diameter, diameter, diameter);

            MeshRenderer meshRenderer = fallback.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                Material material = ResolveFallbackMaterial();
                if (material != null)
                {
                    // sharedMaterial 而不是 material：后者每次访问都会克隆一份材质实例，
                    // 每发弹道泄漏一个材质，长时间对局会稳定增长。
                    meshRenderer.sharedMaterial = material;
                }
            }

            fallback.AddComponent<Projectile>();
            return fallback;
        }

        /// <summary>
        /// 兜底材质：火球橙。只创建一次并复用。
        /// </summary>
        private Material ResolveFallbackMaterial()
        {
            if (fallbackMaterial != null)
            {
                return fallbackMaterial;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                // 项目切到 URP/HDRP 时会走到这里：不报错，弹道仍会生成，只是用引擎默认材质。
                return null;
            }

            fallbackMaterial = new Material(shader);
            fallbackMaterial.color = new Color(1f, 0.45f, 0.1f, 1f);
            fallbackMaterial.SetFloat("_Glossiness", 0.2f);
            fallbackMaterial.SetFloat("_Metallic", 0f);
            return fallbackMaterial;
        }

        private void OnDestroy()
        {
            // 兜底材质是本类运行时创建的，必须自行释放（它不是资产，不会被资源系统回收）。
            if (fallbackMaterial != null)
            {
                Destroy(fallbackMaterial);
                fallbackMaterial = null;
            }
        }

        /// <summary>就"弹道预制体缺失"告警一次。用一次性标记避免每发弹道刷一条日志。</summary>
        private void WarnMissingPrefabOnce()
        {
            if (hasWarnedMissingPrefab)
            {
                return;
            }

            hasWarnedMissingPrefab = true;

            if (createFallbackPrimitiveWhenPrefabMissing)
            {
                Debug.LogWarning(
                    $"[ProjectileSpawner] {name} 未配置弹道预制体，已改用代码生成的白盒球体。" +
                    "请执行「MOBA Demo/一键组装测试战场」以生成并注入 Assets/Prefabs/VFX/Projectile.prefab。", this);
            }
            else
            {
                Debug.LogError(
                    $"[ProjectileSpawner] {name} 未配置弹道预制体且已关闭兜底，所有指向性弹道技能都无法释放。", this);
            }
        }
    }
}

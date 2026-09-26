using UnityEngine;

namespace MOBA.VFX
{
    /// <summary>
    /// 临时改色（表现层）：把单位的全部渲染器换成一份纯色材质，到期后【精确还原】原材质。
    ///
    /// 【用途】白盒期用"施法者整体变黄"表达"自身增益已生效"（Q 致命打击 / 疾行术）。
    /// 这是最省事、在顶视角下最醒目的反馈方式 —— 不需要任何模型或粒子资源。
    ///
    /// 【为什么必须缓存原材质而不是"再换回阵营色"】
    /// 单位当前穿的是哪一份材质，本组件无从判断（英雄预制体的默认材质、`TeamColorView` 在 Start 里
    /// 按「阵营 × 单位类型」换上的四格配色、乃至日后美术阶段接入的真实材质）。
    /// 缓存"改色之前的那一份引用"是唯一不会猜错的还原方式 —— 而且它天然与上色逻辑解耦：
    /// 上色规则以后再怎么改（例如阶段九换成模型 + 贴图），这里一行都不用动。
    ///
    /// 【为什么重叠施法不重新快照】第二次变黄时若再快照一次，快照到的就是【黄色材质】，
    /// 到期"还原"会把单位永久染成黄色 —— 这是最典型的静默外观污染。
    /// 因此只在"从非激活变为激活"的那一次快照，重叠施法只刷新剩余时长。
    ///
    /// 【边界】只写 Renderer.sharedMaterial，不读也不改任何战斗数值。
    /// </summary>
    [DisallowMultipleComponent]
    public class WhiteboxTint : MonoBehaviour
    {
        /// <summary>被改色的渲染器（激活时快照一次）。</summary>
        private Renderer[] renderers;

        /// <summary>改色之前每个渲染器上的材质引用（与 renderers 一一对应）。</summary>
        private Material[] originalMaterials;

        /// <summary>剩余时长（秒）。</summary>
        private float remaining;

        /// <summary>当前是否处于改色状态。</summary>
        private bool isActive;

        /// <summary>当前是否正在改色（只读），供调试与测试断言使用。</summary>
        public bool IsActive => isActive;

        /// <summary>
        /// 让一个单位临时变色。重复调用只刷新时长，不会叠加、也不会污染还原用的快照。
        /// 静态入口而不是要求调用方自己 AddComponent：本组件是"用完即忘"的表现补丁，
        /// 让调用方记住"先查有没有再挂"只会多一处可能写错的簿记。
        /// </summary>
        /// <param name="root">目标单位根节点。</param>
        /// <param name="color">目标颜色。</param>
        /// <param name="duration">持续时长（秒），非正数忽略。</param>
        public static void Apply(GameObject root, Color color, float duration)
        {
            if (root == null || duration <= 0f)
            {
                return;
            }

            Material material = WhiteboxVfxMaterials.GetOpaque(color);

            if (material == null)
            {
                return;
            }

            WhiteboxTint tint = root.GetComponent<WhiteboxTint>();

            if (tint == null)
            {
                // 组件会随单位一起被销毁，因此不需要任何清理登记。
                tint = root.AddComponent<WhiteboxTint>();
            }

            tint.Activate(material, duration);
        }

        private void Activate(Material material, float duration)
        {
            if (!isActive)
            {
                // 只在"从非激活变为激活"时快照原材质（理由见类注释）。
                CaptureOriginals();
                isActive = true;
                remaining = duration;
            }
            else
            {
                // 刷新策略与 BuffComponent 一致：取更晚的结束时间，不缩短已有的长改色。
                remaining = Mathf.Max(remaining, duration);
            }

            ApplyMaterial(material);
        }

        private void Update()
        {
            if (!isActive)
            {
                return;
            }

            remaining -= Time.deltaTime;

            if (remaining > 0f)
            {
                return;
            }

            Restore();
        }

        /// <summary>
        /// 快照当前所有渲染器上的材质引用。
        /// includeInactive = true：与 TeamColorView 同一口径，避免漏掉暂时未激活的子部件。
        /// 这里会分配两个数组，但它只在"第一次改色"时发生一次，不在每帧路径上。
        /// </summary>
        private void CaptureOriginals()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            originalMaterials = new Material[renderers.Length];

            for (int i = 0; i < renderers.Length; i++)
            {
                originalMaterials[i] = renderers[i] != null ? renderers[i].sharedMaterial : null;
            }
        }

        private void ApplyMaterial(Material material)
        {
            if (renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].sharedMaterial = material;
                }
            }
        }

        /// <summary>
        /// 精确还原。组件本身【不】销毁：下一次施法直接复用，省掉一次 AddComponent，
        /// 也避免了"销毁 → 再添加"在 Inspector 里反复增删组件的噪声。
        /// </summary>
        private void Restore()
        {
            isActive = false;
            remaining = 0f;

            if (renderers == null || originalMaterials == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].sharedMaterial = originalMaterials[i];
                }
            }
        }
    }
}

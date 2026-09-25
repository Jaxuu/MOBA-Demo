using UnityEngine;

namespace MOBA.Core
{
    /// <summary>
    /// 实体外观开关（白盒期补丁）：把"让这具尸体立刻从画面上消失"收敛到一个方法里。
    ///
    /// 【为什么需要它】白盒阶段英雄与小兵都是胶囊体 / 方块，死亡后 Collider 与 NavMeshAgent 被禁用
    /// （尸体不再参与索敌与导航），但 MeshRenderer 仍然开着 —— 战场上会堆满站着的"尸体"，
    /// 既看不出谁死谁活，也严重影响对局可读性。
    ///
    /// 【为什么抽成静态工具而不是在两个类里各写一遍】英雄与小兵的死亡收尾分属 HeroController 与
    /// EntityAIController 两个类，遍历子节点渲染器的代码一旦各写一份，将来换渲染方式（例如阶段八接入
    /// 真实模型后改用 Animator 播死亡动画）就会出现"改了一处漏了另一处"的经典问题。
    ///
    /// 【关于"表现与逻辑分离"铁律的边界说明】README §3.4 禁止的是逻辑层引用
    /// Animator / ParticleSystem / AudioSource / UI 组件，并禁止表现反向影响伤害判定；
    /// 本类只写 <c>Renderer.enabled</c> 这一个开关——它是幂等的、结果无关的（关掉它不影响任何伤害数值、
    /// 击杀顺序或胜负），符合铁律的立法意图。
    /// **但它是白盒期的权宜手段**：阶段八接入真实模型与死亡动画后，"死亡表现"应当由表现层
    /// （AnimationComponent / 订阅 OnDied 的表现组件）接管，届时把本类的调用点删掉即可 ——
    /// 这也是把调用点集中成两处（HeroController 与 EntityAIController 各一处）的原因。
    ///
    /// 【性能】GetComponentsInChildren 每次调用会分配一个数组，但本方法只在"死亡 / 复活"这种
    /// 一局只发生几十次的时刻被调用，不在每帧路径上。
    /// </summary>
    public static class EntityVisuals
    {
        /// <summary>
        /// 开关一个实体（含全部子节点）上的所有渲染器。
        /// 用基类 <see cref="Renderer"/> 而不是 MeshRenderer：一次覆盖 MeshRenderer /
        /// SkinnedMeshRenderer（阶段八的模型用得到）以及挂在单位上的粒子渲染器。
        /// </summary>
        /// <param name="root">实体根节点，允许为 null（已被销毁时静默忽略）。</param>
        /// <param name="visible">true = 显示，false = 隐藏（尸体处理）。</param>
        public static void SetRenderersEnabled(GameObject root, bool visible)
        {
            if (root == null)
            {
                return;
            }

            // includeInactive: true —— 单位身上可能有暂时未激活的子节点（例如备用的模型或特效），
            // 漏掉它们会导致复活后"有一半身体没回来"。
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = visible;
                }
            }
        }
    }
}

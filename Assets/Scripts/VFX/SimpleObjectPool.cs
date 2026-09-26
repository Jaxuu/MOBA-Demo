using System.Collections.Generic;
using UnityEngine;

namespace MOBA.VFX
{
    /// <summary>
    /// 最小可用对象池（表现层，阶段九）：把一个特效 Prefab 的"预生成 → 取用 → 归还"账目收敛到一处。
    ///
    /// ==================== 它为什么不是 Core/PrefabPool&lt;T&gt; ====================
    /// 阶段七的 <c>PrefabPool&lt;T&gt;</c> 服务的是**视图组件**（血条 / 飘字）：它按类型池化，
    /// 归还动作只有一句 <c>SetActive(false)</c>，复位逻辑写在视图自己的 OnDisable 里，
    /// 而且它要求模板上**已经**挂着对应的组件（池不认识 GameObject，也不该给模板补组件）。
    ///
    /// 本池服务的是**特效预制体**，三个需求与前者都不同：
    ///   ① 特效是"外部美术给的任意 GameObject"（挂粒子 / 拖尾 / 声音），不可能要求它预先带我们的组件；
    ///   ② 归还时机不由管理器决定，而由**特效自己的播放时长**决定（见 <see cref="PooledVfxInstance"/>）；
    ///   ③ 取用之后还要写位置、旋转、播放参数 —— 这些都是 GameObject 层面的操作。
    ///
    /// 因此它是一个**非泛型的 GameObject 池**：职责更小（只管实例的存亡与层级），
    /// 与 <c>PrefabPool&lt;T&gt;</c> 各服务一类对象，不存在两份"同一件事"的实现。
    ///
    /// ==================== 池耗尽时返回 null，绝不 Instantiate ====================
    /// 这是「表现可丢失」铁律（README §3.4 约束 4）的直接落实：少播一个特效不允许影响对局结果，
    /// 更不允许在战斗中途产生分配抖动（那正是阶段九性能验收要消灭的东西 —— 稳态 GC ≈ 0 B/帧）。
    /// 耗尽只告警一次（重复告警会把 Console 刷满，反而掩盖真正的问题）。
    /// </summary>
    public sealed class SimpleObjectPool
    {
        /// <summary>预生成用的模板。运行时只读，池不会修改它。</summary>
        private readonly GameObject prefab;

        /// <summary>池对象的父节点。归还时把实例挂回这里，保证层级干净、随宿主一起被清理。</summary>
        private readonly Transform host;

        /// <summary>空闲实例（非活跃，等待取用）。</summary>
        private readonly List<GameObject> idle;

        /// <summary>活跃实例（已被取用，尚未归还）。</summary>
        private readonly List<GameObject> active;

        /// <summary>日志标签（模板名），避免每次告警都去取 prefab.name。</summary>
        private readonly string label;

        /// <summary>是否已就"池耗尽"告警过一次。一次性标记，防止战斗中刷屏。</summary>
        private bool hasReportedExhaustion;

        /// <summary>
        /// 构造并预生成。
        /// </summary>
        /// <param name="prefab">特效模板（任意 GameObject 预制体；为 null 时本池不可用，Get 恒返回 null）。</param>
        /// <param name="host">池对象的父节点（通常是 VfxSpawner 自身的 Transform）。</param>
        /// <param name="prewarmCount">预生成数量，即本池的容量上限。</param>
        public SimpleObjectPool(GameObject prefab, Transform host, int prewarmCount)
        {
            this.prefab = prefab;
            this.host = host;
            label = prefab != null ? prefab.name : "（未配置特效预制体）";

            int capacity = Mathf.Max(0, prewarmCount);
            idle = new List<GameObject>(capacity);
            active = new List<GameObject>(capacity);

            Prewarm(capacity);
        }

        /// <summary>本池是否具备工作条件（模板与宿主都在）。</summary>
        public bool IsUsable => prefab != null && host != null;

        /// <summary>当前活跃实例数量（只读），供调试与日志使用。</summary>
        public int ActiveCount => active.Count;

        /// <summary>当前空闲实例数量（只读）。</summary>
        public int IdleCount => idle.Count;

        /// <summary>池的总容量（活跃 + 空闲），只读。</summary>
        public int TotalCount => active.Count + idle.Count;

        /// <summary>
        /// 活跃实例列表，供表现层遍历（例如统一暂停 / 统一回收）。
        ///
        /// 【约束】调用方只许遍历，【不得增删】—— 增删必须走 Get / Release，
        /// 否则池的账目会错（会出现"归还了一个从未取用的实例"这类无法解释的状态）。
        /// 之所以暴露内部列表而不是每帧拷贝一份：本属性可能在每帧被读，拷贝会直接产生每帧 GC。
        /// </summary>
        public List<GameObject> ActiveItems => active;

        /// <summary>
        /// 取一个可用实例（已激活）。池耗尽时返回 null 并告警一次，【绝不】即时 Instantiate。
        /// </summary>
        /// <returns>可用的活跃实例；池耗尽（或本池不可用）时返回 null。</returns>
        public GameObject Get()
        {
            while (idle.Count > 0)
            {
                int lastIndex = idle.Count - 1;
                GameObject candidate = idle[lastIndex];
                idle.RemoveAt(lastIndex);

                // 外部误删（Destroy）会在池里留下空槽：跳过即可，不能把它交出去。
                if (candidate == null)
                {
                    continue;
                }

                candidate.SetActive(true);
                active.Add(candidate);
                return candidate;
            }

            if (prefab != null && !hasReportedExhaustion)
            {
                hasReportedExhaustion = true;

                Debug.LogWarning(
                    $"[SimpleObjectPool] {label} 的池已耗尽（容量 {TotalCount}），本次请求不再生成新实例。" +
                    "表现层缺失不影响对局逻辑；但若持续出现，请调大 VfxSpawner 上的预生成数量。");
            }

            return null;
        }

        /// <summary>
        /// 归还一个实例。幂等：不属于本池、或已被归还过的实例会被安静忽略。
        /// </summary>
        /// <param name="instance">待归还的实例，允许为 null。</param>
        public void Release(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            // 不在活跃清单里 → 说明它不是本池当前借出的对象（重复归还 / 传错池），直接忽略。
            if (!active.Remove(instance))
            {
                return;
            }

            if (host != null)
            {
                instance.transform.SetParent(host, false);
            }

            // 复位局部变换：下次取用时调用方会重新写入世界坐标，
            // 但先归零能保证"刚取出来那一瞬间"的层级与缩放是干净的。
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // 先 SetActive(false)：<see cref="PooledVfxInstance"/> 在自己的 OnDisable 里收尾
            // （停粒子、清跟随目标），因此这一行就是"归还"的全部动作。
            instance.SetActive(false);

            idle.Add(instance);
        }

        /// <summary>归还当前全部活跃实例。用于生成器被禁用 / 对局收尾。</summary>
        public void ReleaseAll()
        {
            // 倒序遍历：Release 会从 active 末尾移除当前元素，倒序可保证索引不失效。
            for (int i = active.Count - 1; i >= 0; i--)
            {
                Release(active[i]);
            }
        }

        /// <summary>
        /// 预生成实例。
        ///
        /// 【为什么 Instantiate 放在这里是可以接受的】它只发生在场景启动（VfxSpawner.OnEnable）时，
        /// 数量固定、时机确定，不构成"战斗中途的分配抖动"；相比之下让工具在场景里预先摆几十个特效对象，
        /// 只会让层级难以阅读，且每次调整模板都要改工具。
        /// </summary>
        /// <param name="count">预生成数量。</param>
        private void Prewarm(int count)
        {
            if (prefab == null || host == null || count <= 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                GameObject instance = UnityEngine.Object.Instantiate(prefab, host);

                // 改名只为在层级面板里可读（池对象都叫 XXX(Clone) 会很难排查）。
                // 字符串拼接发生在预生成阶段，不在战斗稳态路径上。
                instance.name = $"{label}_{i:00}";

                instance.SetActive(false);
                idle.Add(instance);
            }
        }
    }
}

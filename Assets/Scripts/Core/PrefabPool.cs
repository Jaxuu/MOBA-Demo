using System.Collections.Generic;
using UnityEngine;

namespace MOBA.Core
{
    /// <summary>
    /// 泛型组件对象池（阶段七）：把"预生成 → 取用 → 归还"这套账目收敛到一处。
    ///
    /// 服务对象：头顶血条、伤害飘字（阶段七），以及阶段八的受击特效 / 弹道表现。
    /// 刻意【不】泛化到实体本身 —— README §6.2 明确「对象池只覆盖弹道、特效与 UI 元素；
    /// 不提前泛化到全部实体，也不引入全局事件总线」。
    ///
    /// 【为什么不做成 MonoBehaviour】池是纯数据结构，挂到场景里只会多一个没有意义的对象。
    /// 由消费方（管理器）持有并驱动。
    ///
    /// 【池耗尽时返回 null，绝不 Instantiate】这是"表现可丢失"铁律的直接落实：
    /// 血条少画一条、飘字少飘一个，都不允许影响对局结果，也不允许在战斗中途产生分配抖动。
    /// 耗尽只告警一次（重复告警会把 Console 刷满，反而掩盖真正的问题）。
    ///
    /// 【归还时只做 SetActive(false)】视图组件在自己的 OnDisable 里退订事件、复位状态，
    /// 因此池不必认识任何具体的视图类型 —— 这也是它能够保持泛型的原因。
    /// </summary>
    /// <typeparam name="T">池化对象类型，必须是组件（需要它的 gameObject 与 transform）。</typeparam>
    public class PrefabPool<T> where T : Component
    {
        /// <summary>预生成用的模板。运行时只读，池不会修改它。</summary>
        private readonly T prefab;

        /// <summary>池对象的父节点。归还时会把实例挂回这里，保证层级干净、随宿主一起被清理。</summary>
        private readonly Transform host;

        /// <summary>空闲实例（非活跃，等待取用）。</summary>
        private readonly List<T> idle;

        /// <summary>活跃实例（已被取用，尚未归还）。</summary>
        private readonly List<T> active;

        /// <summary>日志用的标签（模板名），避免每次告警都去取 prefab.name。</summary>
        private readonly string label;

        /// <summary>是否已经就"池耗尽"告警过一次。一次性标记，防止战斗中刷屏。</summary>
        private bool hasReportedExhaustion;

        /// <summary>
        /// 构造并预生成。
        /// </summary>
        /// <param name="prefab">模板实例（通常是一键组装工具生成的场景模板，处于非激活状态）。</param>
        /// <param name="host">池对象的父节点。</param>
        /// <param name="prewarmCount">预生成数量。即本池的容量上限。</param>
        public PrefabPool(T prefab, Transform host, int prewarmCount)
        {
            this.prefab = prefab;
            this.host = host;
            label = prefab != null ? prefab.name : typeof(T).Name;

            int capacity = Mathf.Max(0, prewarmCount);
            idle = new List<T>(capacity);
            active = new List<T>(capacity);

            Prewarm(capacity);
        }

        /// <summary>当前活跃实例数量（只读），供调试与日志使用。</summary>
        public int ActiveCount => active.Count;

        /// <summary>当前空闲实例数量（只读）。</summary>
        public int IdleCount => idle.Count;

        /// <summary>池的总容量（活跃 + 空闲），只读。</summary>
        public int TotalCount => active.Count + idle.Count;

        /// <summary>
        /// 活跃实例列表，供表现层每帧遍历驱动（例如血条的朝向与缩放、飘字的上浮与淡出）。
        ///
        /// 【约束】调用方只许遍历，【不得增删】——增删必须走 Get / Release，
        /// 否则池的账目会错（会出现"归还了一个从未取用的实例"这类无法解释的状态）。
        /// 之所以暴露内部列表而不是每帧拷贝一份：本方法每帧都会被调用，拷贝会直接产生每帧 GC。
        /// </summary>
        public List<T> ActiveItems => active;

        /// <summary>
        /// 取一个可用实例（已激活）。池耗尽时返回 null 并告警一次，【绝不】即时 Instantiate。
        /// </summary>
        /// <returns>可用的活跃实例；池耗尽时返回 null。</returns>
        public T Get()
        {
            while (idle.Count > 0)
            {
                int lastIndex = idle.Count - 1;
                T candidate = idle[lastIndex];
                idle.RemoveAt(lastIndex);

                // 外部误删（Destroy）会在池里留下空槽：跳过即可，不能把它交出去。
                if (candidate == null)
                {
                    continue;
                }

                candidate.gameObject.SetActive(true);
                active.Add(candidate);
                return candidate;
            }

            if (!hasReportedExhaustion)
            {
                hasReportedExhaustion = true;
                Debug.LogWarning(
                    $"[PrefabPool] {label} 的池已耗尽（容量 {TotalCount}），本次请求不再生成新实例。" +
                    "表现层缺失不影响对局逻辑；但若持续出现，请调大预生成数量。");
            }

            return null;
        }

        /// <summary>
        /// 归还一个实例。幂等：不属于本池、或已被归还过的实例会被安静忽略。
        /// </summary>
        /// <param name="item">待归还的实例，允许为 null。</param>
        public void Release(T item)
        {
            if (item == null)
            {
                return;
            }

            // 不在活跃清单里 → 说明它不是本池当前借出的对象（重复归还 / 传错池），直接忽略。
            if (!active.Remove(item))
            {
                return;
            }

            if (host != null)
            {
                item.transform.SetParent(host, false);
            }

            // 复位局部变换：下次取用时视图会重新写入世界坐标，
            // 但先归零能保证"刚取出来那一瞬间"的层级与缩放是干净的（尤其是被父节点缩放过的情形）。
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.transform.localScale = Vector3.one;

            // 先 SetActive(false)：视图组件在自己的 OnDisable 里退订事件、清空绑定，
            // 因此这一行就是"归还"的全部动作，池不需要认识具体视图类型。
            item.gameObject.SetActive(false);

            idle.Add(item);
        }

        /// <summary>归还当前全部活跃实例。用于管理器被禁用 / 场景收尾。</summary>
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
        /// 【为什么 Instantiate 放在这里是可以接受的】它只发生一次（场景启动时），
        /// 数量固定、时机确定，不构成"战斗中途的分配抖动"；相比之下让工具在场景里预先摆 24 个血条对象，
        /// 只会让场景层级变得难以阅读，且每次调整模板都要改工具。
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
                T instance = Object.Instantiate(prefab, host);

                // 改名只为在层级面板里可读（池对象都叫 Template(Clone) 会很难排查）。
                // 字符串拼接发生在预生成阶段，不影响战斗稳态。
                instance.name = $"{label}_{i:00}";

                instance.gameObject.SetActive(false);
                idle.Add(instance);
            }
        }
    }
}

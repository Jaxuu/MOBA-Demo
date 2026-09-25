using System.Collections.Generic;
using UnityEngine;

namespace MOBA.Core
{
    /// <summary>
    /// 实体注册表：维护「当前参与对局的全部 EntityBase」清单，供对局层做全局操作
    /// （当前唯一用途：对局结束时冻结战场）。
    ///
    /// 为什么需要它，而不是在需要时直接 FindObjectsOfType：
    /// 1. 全场景扫描是一次 O(场景对象数) 的分配型操作，且它表达不出「谁算参战单位」这一语义；
    /// 2. 注册表把「单位从哪来」统一收口——预制体预置、MinionSpawner 生成、测试代码创建，
    ///    只要挂了 EntityBase 就会自动登记，后续新增单位类型时调用方一行都不用改。
    ///
    /// 登记时机：EntityBase 的 OnEnable / OnDisable。
    /// Unity 保证物体被销毁前一定先走 OnDisable，因此清单里不会残留已销毁对象；
    /// 「物体被临时禁用」也会自动退出清单，语义上等价于「该单位暂不参与对局」。
    /// </summary>
    public static class EntityRegistry
    {
        /// <summary>当前登记的实体清单。只允许本类修改，外部只能经 Snapshot 取得副本。</summary>
        private static readonly List<EntityBase> entities = new List<EntityBase>();

        /// <summary>当前登记的实体数量（只读），供调试与日志使用。</summary>
        public static int Count => entities.Count;

        /// <summary>
        /// 登记一个实体。
        /// 重复登记会被忽略：同一物体反复启用/禁用时 OnEnable 可能被多次调用，
        /// 若不去重，清单里会留下同一实体的多份引用，冻结战场时会被重复操作。
        /// </summary>
        /// <param name="entity">待登记的实体，传 null 时忽略。</param>
        public static void Register(EntityBase entity)
        {
            if (entity == null || entities.Contains(entity))
            {
                return;
            }

            entities.Add(entity);
        }

        /// <summary>
        /// 注销一个实体。
        /// 用 List.Remove 而不是先查索引再删除：该实体从未登记过时 Remove 只返回 false、不会抛异常，
        /// 因此本方法天然幂等，OnDisable 被重复触发也不会出问题。
        /// </summary>
        /// <param name="entity">待注销的实体，传 null 时忽略。</param>
        public static void Unregister(EntityBase entity)
        {
            if (entity == null)
            {
                return;
            }

            entities.Remove(entity);
        }

        /// <summary>
        /// 取当前清单的快照。
        /// 返回副本而不是直接暴露内部 List 的原因：调用方（冻结战场）会在遍历过程中停用组件，
        /// 若期间有实体被启用/禁用（OnEnable/OnDisable 会增删清单），
        /// 直接遍历内部 List 会抛 InvalidOperationException（集合被修改）。
        /// 本方法每局只调用一次，这点分配完全可以接受。
        /// </summary>
        /// <returns>当前全部登记实体的数组副本；可能含 null 槽位，调用方需判空。</returns>
        public static EntityBase[] Snapshot()
        {
            return entities.ToArray();
        }

        /// <summary>
        /// 重置静态清单。
        /// 必要性：静态字段的生命周期属于「类型」而非「实例」。关闭 Domain Reload 时退出播放模式
        /// 不会重新加载程序集，上一局的实体引用会残留，冻结战场时会遍历到已销毁对象。
        /// SubsystemRegistration 阶段早于任何场景加载与 Awake，是官方推荐的静态状态重置时机
        /// （与 BaseCoreController.ResetStaticState 同一处理）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            entities.Clear();
        }
    }
}

using UnityEngine;

namespace MOBA.VFX
{
    /// <summary>
    /// 池化特效实例的自生命周期组件（表现层，阶段九）：挂在每个由 <see cref="SimpleObjectPool"/>
    /// 借出的特效实例上，负责"播多久、跟谁走、什么时候还回池子"。
    ///
    /// ==================== 为什么需要一个独立组件，而不是在 VfxSpawner 里维护一张表 ====================
    /// 特效是"生成即忘"的：生成器只管借出，回收条件却要看特效自己 —— 它挂了多久、
    /// 身上有几个粒子系统、各自的播放时长是多少。把这些写进生成器，生成器就要为每个特效
    /// 记一份状态并每帧遍历（还要多一次组件查找）。
    ///
    /// 让特效自己管自己，好处有三：
    ///   ① 生成器零簿记 —— 它只需要 <c>Get()</c> 之后调一次 <c>Play()</c>；
    ///   ② 回收时机是"真的播完了"，而不是"估算的时间到了"（见 <see cref="MeasureParticleLifetime"/>）；
    ///   ③ 与白盒期的 <c>WhiteboxVfxLifetime</c> 是同一套设计（生成即忘 + 自回收），
    ///      两者可以并存、日后删掉白盒那一份时不需要动生成器。
    ///
    /// ==================== 为什么用 Update 计时而不是 Destroy(gameObject, delay) ====================
    /// 池化对象**不能被销毁**（销毁了池就空了）。因此回收动作是"还回池子"而不是 Destroy，
    /// 而"还回池子"这件事必须由本组件在合适的时机主动发起。
    ///
    /// 【边界】本组件只写自己的 Transform 与自己的存亡，不读也不改任何战斗数值；
    /// 关掉整个特效系统，对局结果完全不变（README §3.4 约束 4）。
    /// </summary>
    [DisallowMultipleComponent]
    public class PooledVfxInstance : MonoBehaviour
    {
        /// <summary>
        /// 兜底播放时长（秒）。
        /// 用途：特效身上既没有粒子系统、调用方也没给显式时长时，至少让它存在一小会儿再回收 ——
        /// 否则一个忘了配粒子系统的预制体会"取出来就被还回去"，看起来像完全没生效，
        /// 而 Console 里没有任何线索。
        /// </summary>
        [SerializeField] private float fallbackLifetime = 1f;

        /// <summary>所属的池。由 Play 注入（本组件不自己 new 池，也不持有预制体）。</summary>
        private SimpleObjectPool owner;

        /// <summary>自身（含子节点）上的全部粒子系统，首次播放时缓存。</summary>
        private ParticleSystem[] particleSystems;

        /// <summary>跟随目标；为 null 表示固定在生成点。</summary>
        private Transform followTarget;

        /// <summary>跟随偏移（相对跟随目标的世界空间向量）。</summary>
        private Vector3 followOffset;

        /// <summary>本次播放的开始时刻（Time.time 基准）。</summary>
        private float playTime;

        /// <summary>本次播放的总时长（秒），> 0。</summary>
        private float lifetime;

        /// <summary>是否正在播放。为 false 时 Update 不做任何事。</summary>
        private bool isPlaying;

        /// <summary>是否正在播放（只读），供调试与测试断言使用。</summary>
        public bool IsPlaying => isPlaying;

        /// <summary>剩余播放时间（秒）；未在播放时返回 0。</summary>
        public float RemainingLifetime => isPlaying ? Mathf.Max(0f, lifetime - (Time.time - playTime)) : 0f;

        /// <summary>
        /// 开始一次播放。由生成器在 <c>SimpleObjectPool.Get()</c> 之后立即调用。
        /// </summary>
        /// <param name="pool">所属池（回收时用）。允许为 null —— 此时只播不回收，用于调试挂载。</param>
        /// <param name="maxLifetime">
        /// 显式播放时长（秒）。&gt; 0 时直接采用（**循环特效必须传它**，否则会被当成"播不完"）；
        /// &lt;= 0 时按粒子系统的实际时长自动推算（见 <see cref="MeasureParticleLifetime"/>）。
        /// </param>
        /// <param name="follow">跟随目标；null = 固定在生成点。</param>
        /// <param name="offset">跟随偏移。</param>
        public void Play(SimpleObjectPool pool, float maxLifetime, Transform follow, Vector3 offset)
        {
            owner = pool;
            followTarget = follow;
            followOffset = offset;

            CacheParticleSystems();
            RestartParticles();

            lifetime = ResolveLifetime(maxLifetime);
            playTime = Time.time;
            isPlaying = true;

            // 立即对齐一次位置：否则第一帧会停在池宿主（VfxSpawner）的原点，
            // 观感上是"特效先在场地中央闪一下，再跳到命中点"。
            ApplyFollow();
        }

        /// <summary>
        /// 立刻结束播放并归还池子。幂等。
        /// 供生成器在"整块关闭特效"时统一清场，或调试时手动回收。
        /// </summary>
        public void ReleaseNow()
        {
            if (!isPlaying)
            {
                return;
            }

            ReturnToPool();
        }

        /// <summary>
        /// 每帧推进：先处理跟随，再判断是否播完。
        /// 顺序不可颠倒 —— 播完的那一帧仍然要先把位置更新到位，否则最后一帧会留在上一帧的位置上。
        /// </summary>
        private void Update()
        {
            if (!isPlaying)
            {
                return;
            }

            ApplyFollow();

            if (Time.time - playTime >= lifetime)
            {
                ReturnToPool();
            }
        }

        /// <summary>
        /// 被禁用即视为"本次播放结束"。
        /// 走到这里有两条路径：① 池归还我们（<see cref="ReturnToPool"/> 已先把 isPlaying 置 false）；
        /// ② 外部（整棵层级被禁用 / 场景卸载）直接把我们关掉。
        /// 两种情况都只需要清理自己的状态，**不能再调 owner.Release**（那会造成递归归还）。
        /// </summary>
        private void OnDisable()
        {
            isPlaying = false;
            followTarget = null;
            followOffset = Vector3.zero;
            playTime = 0f;
            lifetime = 0f;
        }

        /// <summary>
        /// 归还池子。先清自己的状态再调池 —— 顺序很关键：
        /// 池的 Release 会 <c>SetActive(false)</c>，而 OnDisable 里看到"仍在播放"就会出问题。
        /// </summary>
        private void ReturnToPool()
        {
            isPlaying = false;

            SimpleObjectPool pool = owner;
            owner = null;

            if (pool != null)
            {
                pool.Release(gameObject);
            }
            else
            {
                // 没有池（调试期手工挂载）：只把自己关掉，不销毁（销毁会连宿主一起出问题）。
                gameObject.SetActive(false);
            }
        }

        /// <summary>把实例摆到"跟随目标 + 偏移"的位置；无跟随目标时保持原地不动。</summary>
        private void ApplyFollow()
        {
            if (followTarget == null)
            {
                return;
            }

            transform.position = followTarget.position + followOffset;
        }

        /// <summary>
        /// 决定本次播放多久。
        /// 优先级：调用方显式给值 &gt; 粒子系统实际时长 &gt; 兜底时长。
        /// </summary>
        /// <param name="maxLifetime">调用方给的显式时长（&lt;= 0 表示不指定）。</param>
        /// <returns>播放时长（秒），恒 &gt; 0。</returns>
        private float ResolveLifetime(float maxLifetime)
        {
            if (maxLifetime > 0f)
            {
                return maxLifetime;
            }

            float measured = MeasureParticleLifetime();

            if (measured > 0f)
            {
                return measured;
            }

            return Mathf.Max(0.01f, fallbackLifetime);
        }

        /// <summary>
        /// 按粒子系统推算"这一发特效总共要播多久"（取所有系统的最大值）。
        ///
        /// 公式 = 系统的 duration + 发射延迟 + 粒子最长存活时间。
        /// 三项缺一不可：只算 duration 会让"延迟 0.5 秒才开始冒烟"的特效被提前回收，
        /// 只算 duration + lifetime 又会漏掉 startDelay。
        ///
        /// 【已知边界】对 <c>looping = true</c> 的系统，duration 表示"一个循环的长度"而不是总时长，
        /// 推算结果必然偏小 —— 这类特效请由调用方显式传 maxLifetime（VfxSpawner 的字段就是干这个的）。
        /// </summary>
        /// <returns>推算出的总时长（秒）；没有粒子系统时返回 0。</returns>
        private float MeasureParticleLifetime()
        {
            if (particleSystems == null || particleSystems.Length == 0)
            {
                return 0f;
            }

            float maxLifetime = 0f;

            for (int i = 0; i < particleSystems.Length; i++)
            {
                ParticleSystem system = particleSystems[i];

                if (system == null)
                {
                    continue;
                }

                // MainModule 是结构体包装（取值不产生堆分配），三个字段都是只读查询。
                ParticleSystem.MainModule main = system.main;

                float total = main.duration
                    + main.startDelay.constantMax
                    + main.startLifetime.constantMax;

                if (total > maxLifetime)
                {
                    maxLifetime = total;
                }
            }

            return maxLifetime;
        }

        /// <summary>缓存自身与子节点上的粒子系统（includeInactive，避免漏掉未激活的子特效）。</summary>
        private void CacheParticleSystems()
        {
            if (particleSystems != null && particleSystems.Length > 0)
            {
                return;
            }

            particleSystems = GetComponentsInChildren<ParticleSystem>(true);
        }

        /// <summary>
        /// 重启粒子系统。
        /// 【为什么必须 Clear 再 Play】池化实例上一轮播完时可能还残留着未消亡的粒子，
        /// 直接 Play 会让上一发的尾巴与新的一发叠在一起（表现为"特效越打越浓"）。
        /// </summary>
        private void RestartParticles()
        {
            if (particleSystems == null)
            {
                return;
            }

            for (int i = 0; i < particleSystems.Length; i++)
            {
                ParticleSystem system = particleSystems[i];

                if (system == null)
                {
                    continue;
                }

                // withChildren = true：一次覆盖它自己的子发射器。
                system.Clear(true);
                system.Play(true);
            }
        }
    }
}

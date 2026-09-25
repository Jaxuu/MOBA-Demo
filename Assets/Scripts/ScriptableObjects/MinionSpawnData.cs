using UnityEngine;

namespace MOBA.Data
{
    /// <summary>
    /// 小兵波次生成配置模板（只读）。
    /// 职责边界：只描述「一波出几个、波内隔多久出一个、隔多久来下一波」，
    /// 不持有任何运行时状态（已生成数量、生成计时器、当前波次索引）——
    /// 那些必须由 MinionSpawner 实例自己保存，因为 ScriptableObject 是工程内共享资产，
    /// 一旦写回会在编辑器与打包环境下互相污染，且退出播放模式后不会自动还原。
    /// </summary>
    [CreateAssetMenu(
        fileName = "MinionSpawnData_New",
        menuName = "MOBA/Minion Spawn Data",
        order = 2)]
    public class MinionSpawnData : ScriptableObject
    {
        [Header("单位配置")]
        [Tooltip("小兵预制体。由 MinionSpawner 实例化，预制体上必须挂载 EntityBase 并配置好 EntityStatsData。")]
        [SerializeField] private GameObject minionPrefab;

        [Header("波次节奏")]
        [Tooltip("每波生成的小兵数量。")]
        [Min(1)]
        [SerializeField] private int waveCount = 3;

        [Tooltip("同一波次内相邻两个小兵的生成间隔（秒），用于错开出生位置、避免同一帧全部挤在出生点。")]
        [Min(0f)]
        [SerializeField] private float spawnInterval = 0.5f;

        [Tooltip("波次之间的间隔（秒）。MinionSpawner 以「上一波首个小兵出生时刻」为基准计时。")]
        [Min(0f)]
        [SerializeField] private float waveInterval = 15f;

        /// <summary>小兵预制体引用。可能为 null，使用方需判空并报错，不要静默跳过。</summary>
        public GameObject MinionPrefab => minionPrefab;

        /// <summary>每波生成数量。</summary>
        public int WaveCount => waveCount;

        /// <summary>同波次内相邻小兵的生成间隔（秒）。</summary>
        public float SpawnInterval => spawnInterval;

        /// <summary>波次之间的间隔（秒）。</summary>
        public float WaveInterval => waveInterval;
    }
}

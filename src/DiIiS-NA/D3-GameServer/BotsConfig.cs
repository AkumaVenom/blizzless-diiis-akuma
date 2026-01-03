using DiIiS_NA.Core.Config;

namespace DiIiS_NA.D3_GameServer
{
	/// <summary>
	/// Server-side bot simulation settings.
	/// Controlled via the [Bots] section in config.ini.
	/// </summary>
	public sealed class BotsConfig : Config
	{
		/// <summary>
		/// Master toggle for the entire bot system.
		/// </summary>
		public bool Enabled
		{
			get => GetBoolean(nameof(Enabled), false);
			set => Set(nameof(Enabled), value);
		}

		/// <summary>
		/// Maximum number of combat bots to spawn per world.
		/// </summary>
		public int MaxBotsPerWorld
		{
			get => GetInt(nameof(MaxBotsPerWorld), 10);
			set => Set(nameof(MaxBotsPerWorld), value);
		}

		/// <summary>
		/// Number of "fun" interactive bots to spawn in town worlds.
		/// </summary>
		public int TownBotsPerTown
		{
			get => GetInt(nameof(TownBotsPerTown), 3);
			set => Set(nameof(TownBotsPerTown), value);
		}

		/// <summary>
		/// Minimum respawn delay for monsters (in seconds).
		/// </summary>
		public int MobRespawnMinSeconds
		{
			get => GetInt(nameof(MobRespawnMinSeconds), 180);
			set => Set(nameof(MobRespawnMinSeconds), value);
		}

		/// <summary>
		/// Maximum respawn delay for monsters (in seconds).
		/// </summary>
		public int MobRespawnMaxSeconds
		{
			get => GetInt(nameof(MobRespawnMaxSeconds), 300);
			set => Set(nameof(MobRespawnMaxSeconds), value);
		}

		public static BotsConfig Instance { get; } = new();

		private BotsConfig() : base("Bots")
		{
		}
	}
}

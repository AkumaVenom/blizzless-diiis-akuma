using DiIiS_NA.Core.Config;

namespace DiIiS_NA.D3_GameServer
{
    /// <summary>
    /// Akuma custom gameplay feature toggles.
    /// Controlled via the [Akuma-Features] section in config.ini.
    /// </summary>
    public sealed class AkumaFeaturesConfig : Config
    {
        /// <summary>
        /// "Akuma's Hell on Earth" - replaces regular monsters (including champions/rares/minions)
        /// with random demon-family monsters (quest actors & bosses are excluded).
        /// </summary>
        public bool HellOnEarthEnabled
        {
            get => GetBoolean(nameof(HellOnEarthEnabled), false);
            set => Set(nameof(HellOnEarthEnabled), value);
        }
        /// <summary>
        /// "Akuma's High Mob Density" - increases monster pack density/spawn counts generated in scenes.
        /// </summary>
        public bool HighMobDensityEnabled
        {
            get => GetBoolean(nameof(HighMobDensityEnabled), false);
            set => Set(nameof(HighMobDensityEnabled), value);
        }


        public static AkumaFeaturesConfig Instance { get; } = new();

        private AkumaFeaturesConfig() : base("Akuma-Features")
        {
        }
    }
}

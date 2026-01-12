using DiIiS_NA.D3_GameServer.Core.Types.SNO;
using DiIiS_NA.GameServer.Core.Types.TagMap;
using DiIiS_NA.GameServer.MessageSystem;

namespace DiIiS_NA.GameServer.GSSystem.ActorSystem.Implementations
{
    
    [HandledSNO(ActorSno._p6_necro_corpse_flesh)]
    class NecromancerFlesh : Gizmo
    {
        internal int SpawnTick { get; private set; }
        internal int ExpireTick { get; private set; }
        /// <summary>
        /// Set to true when this corpse is removed by server-side cleanup logic.
        /// Used to suppress on-destroy side effects that are meant to happen only when a corpse is consumed.
        /// </summary>
        internal bool SuppressOnDestroyEffects { get; set; } = false;

        public NecromancerFlesh(MapSystem.World world, ActorSno sno, TagMap tags)
            : base(world, sno, tags)
        {
            SpawnTick = world?.Game?.TickCounter ?? 0;
            var game = world?.Game;
            if (game != null)
                ExpireTick = SpawnTick + (int)(1000f / game.UpdateFrequency * game.TickRate * 60f);
            else
                ExpireTick = SpawnTick;

            Field2 = 16;//16;
            Field7 = 0x00000001;
            CollFlags = 1; // this.CollFlags = 0; a hack for passing through blockers /fasbat
            Attributes[GameAttributes.Hitpoints_Cur] = 1;
        }

    }
    //*/
}

//454066

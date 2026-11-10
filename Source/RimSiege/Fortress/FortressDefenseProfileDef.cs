using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressDefenseProfileDef : Def
    {
        public TechLevel minTech;
        public List<ThingDef> machineGuns = new();
        public FortressGunOption wallSupport;
        public List<ThingDef> cornerTurrets = new();
        public ThingDef imperialCorner;
        public ThingDef hunterTrap;
        public bool middleMachineGuns;
        public List<FortressGunOption> smallRoof = new();
        public List<FortressGunOption> largeRoof = new();
        public List<FortressGunOption> mortars = new();

        internal static FortressDefenseProfileDef For(Faction faction) =>
            DefDatabase<FortressDefenseProfileDef>.AllDefsListForReading
                .Where(def => def.minTech <= faction.def.techLevel)
                .MaxBy(def => def.minTech);
    }

    public sealed class FortressGunOption
    {
        public ThingDef turret;
        public ThingDef ammo;
        public float minPoints;
        public int count = 1;
        public bool centered;
    }
}

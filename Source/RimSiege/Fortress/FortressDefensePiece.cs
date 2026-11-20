using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    // Blueprint positions stay independent of the floor-band offsets of a generated map.
    internal sealed class FortressDefensePiece : IExposable
    {
        internal ThingDef Def;
        internal ThingDef Ammo;
        internal IntVec3 Ground;
        internal int Floor;
        internal Rot4 Rotation;
        internal int CoveredSectors;

        internal IntVec3 CellOn(Map map) => Api.CellOnFloor(Ground, Floor, map);

        public void ExposeData()
        {
            Scribe_Defs.Look(ref Def, "def");
            Scribe_Defs.Look(ref Ammo, "ammo");
            Scribe_Values.Look(ref Ground, "ground");
            Scribe_Values.Look(ref Floor, "floor");
            Scribe_Values.Look(ref Rotation, "rotation");
            Scribe_Values.Look(ref CoveredSectors, "coveredSectors");
        }
    }
}

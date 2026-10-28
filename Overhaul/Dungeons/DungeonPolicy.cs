using System;
using System.Collections.Generic;

namespace Overhaul.Dungeons
{
    // Explicit lists: a new game location must never become destructively eligible by accident.
    internal static class DungeonPolicy
    {
        internal const string DefaultResetLocations = "Crypt2,Crypt3,Crypt4,SunkenCrypt4,MountainCave02,Mistlands_DvergrTownEntrance1,Mistlands_DvergrTownEntrance2,Hildir_crypt,Hildir_cave,Hildir_plainsfortress,MorkBorg,TheHole01,HalfBurried_ForestCrypt,GoblinCamp2,GoblinCamp2_1,FortressRuins,NorthVillage,TrollCave02,BearCave,MorgenHole1,MorgenHole2,MorgenHole3,TheDarkestHole,DN_Bossroom,CharredFortress," + MistlandsSurface;
        // Dvergr excavation camps, dvergr towers (whole and ruined) and the petrified giants (their bone deposits), added in 2.3.0.
        internal const string MistlandsSurface = "Mistlands_Excavation1,Mistlands_Excavation2,Mistlands_Excavation3,Mistlands_GuardTower1_new,Mistlands_GuardTower2_new,Mistlands_GuardTower3_new,Mistlands_GuardTower1_ruined_new,Mistlands_GuardTower1_ruined_new2,Mistlands_GuardTower3_ruined_new,Mistlands_Giant1,Mistlands_Giant2";
        internal static readonly HashSet<string> Supported = new HashSet<string>(DefaultResetLocations.Split(','), StringComparer.Ordinal);
        internal static readonly HashSet<string> ScaledGenerators = new HashSet<string>(StringComparer.Ordinal)
        {
            "DG_ForestCrypt", "DG_SunkenCrypt", "DG_Cave", "DG_DvergrTown",
            "DG_Hildir_ForestCrypt", "DG_Hildir_Cave", "DG_MorkHalla", "DG_Hole"
        };

        internal static string PrefabName(string name)
        {
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7) : name;
        }

        internal static void Scale(int originalMin, int originalMax, double multiplier, out int min, out int max)
        {
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0) multiplier = 1;
            max = (int)Math.Max(1, Math.Min(96, Math.Round(originalMax * multiplier, MidpointRounding.AwayFromZero)));
            min = (int)Math.Max(0, Math.Min(max, Math.Round(originalMin * multiplier, MidpointRounding.AwayFromZero)));
        }

        internal static bool IsDue(long lastTicks, long nowTicks, double hours)
        {
            return hours > 0 && !double.IsNaN(hours) && !double.IsInfinity(hours) && lastTicks > 0 &&
                   nowTicks >= lastTicks && (nowTicks - lastTicks) / (double)TimeSpan.TicksPerHour >= hours;
        }

        internal static int NextSeed(int previous, int random)
        {
            // No reserved int.MinValue (native force-seed sentinel), including on overflow.
            if (random == int.MinValue) random = 0;
            if (random == previous) random = previous == int.MaxValue ? 0 : previous + 1;
            return random == int.MinValue ? 1 : random;
        }
    }
}

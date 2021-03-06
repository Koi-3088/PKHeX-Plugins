using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PKHeX.Core.AutoMod
{
    /// <summary>
    /// Dual-approach legalization methods (regenerate and brute force)
    /// </summary>
    public static class Legalizer
    {
        /// <summary>
        /// Global legalizer settings. Ideally everything should be solved via the API.
        /// If something gets solved via bruteforce, something is wrong.
        /// </summary>
        public static bool AllowAPI { get; set; } = true;
        public static bool AllowBruteForce { get; set; } = true;

        /// <summary>
        /// Tries to regenerate the <see cref="pk"/> into a valid pkm.
        /// </summary>
        /// <param name="pk">Currently invalid pkm data</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM Legalize(this PKM pk)
        {
            var tr = TrainerSettings.GetSavedTrainerData(pk.Format);
            return tr.Legalize(pk);
        }

        /// <summary>
        /// Tries to regenerate the <see cref="pk"/> into a valid pkm.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="pk">Currently invalid pkm data</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM Legalize(this ITrainerInfo tr, PKM pk)
        {
            var set = new RegenTemplate(pk)
            {
                Ball = (Ball) pk.Ball,
                ShinyType = pk.ShinyXor == 0 ? Shiny.AlwaysSquare : pk.IsShiny ? Shiny.AlwaysStar : Shiny.Never
            };
            var legal = tr.GetLegalFromTemplate(pk, set, out var satisfied);
            if (satisfied)
                return legal;

            var dest = new PokeTrainerDetails(pk.Clone());
            var resetForm = ShowdownUtil.IsInvalidForm(set.Form);
            legal = BruteForce.ApplyDetails(pk, set, resetForm, dest);
            legal.SetTrainerData(dest);
            return legal;
        }

        /// <summary>
        /// Imports <see cref="sets"/> to a provided <see cref="arr"/>, with a context of <see cref="tr"/>.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="sets">Set data to import</param>
        /// <param name="arr">Current list of data to write to</param>
        /// <param name="start">Starting offset to place converted details</param>
        /// <param name="overwrite">Overwrite</param>
        /// <returns>Result code indicating success or failure</returns>
        public static AutoModErrorCode ImportToExisting(this SaveFile tr, IReadOnlyList<ShowdownSet> sets, IList<PKM> arr, int start = 0, bool overwrite = true)
        {
            var emptySlots = overwrite
                ? Enumerable.Range(start, sets.Count).Where(set => set < arr.Count).ToList()
                : FindAllEmptySlots(arr, start);

            if (emptySlots.Count < sets.Count)
                return AutoModErrorCode.NotEnoughSpace;

            var generated = 0;
            var invalidAPISets = new List<ShowdownSet>();
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                var regen = new RegenTemplate(set);
                if (set.InvalidLines.Count > 0)
                    return AutoModErrorCode.InvalidLines;

                Debug.WriteLine($"Generating Set: {GameInfo.Strings.Species[set.Species]}");
                var pk = tr.GetLegalFromSet(regen, out var msg);
                pk.ResetPartyStats();
                pk.SetBoxForm();
                if (msg == LegalizationResult.BruteForce)
                    invalidAPISets.Add(set);

                var index = emptySlots[i];
                tr.SetBoxSlotAtIndex(pk, index);
                generated++;
            }

            Debug.WriteLine($"API Genned Sets: {generated - invalidAPISets.Count}/{generated}, {invalidAPISets.Count} were not.");
            foreach (var set in invalidAPISets)
                Debug.WriteLine(set.Text);
            return AutoModErrorCode.None;
        }

        /// <summary>
        /// Imports a <see cref="set"/> to create a new <see cref="PKM"/> with a context of <see cref="tr"/>.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="set">Set data to import</param>
        /// <param name="msg">Result code indicating success or failure</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM GetLegalFromSet(this ITrainerInfo tr, IBattleTemplate set, out LegalizationResult msg)
        {
            var template = PKMConverter.GetBlank(tr.Generation, (GameVersion)tr.Game);
            template.ApplySetDetails(set);
            return tr.GetLegalFromSet(set, template, out msg);
        }

        /// <summary>
        /// Main method that calls both API legality and Bruteforce
        /// </summary>
        /// <param name="tr">Trainer Data that was passed in</param>
        /// <param name="set">Showdown set being used</param>
        /// <param name="template">template PKM to legalize</param>
        /// <param name="msg">Legalization result (API, Bruteforce, Failure)</param>
        /// <returns>Legalized pkm</returns>
        private static PKM GetLegalFromSet(this ITrainerInfo tr, IBattleTemplate set, PKM template, out LegalizationResult msg)
        {
            if (set is ShowdownSet s)
                set = new RegenTemplate(s);

            if (AllowAPI)
            {
                bool success = tr.TryAPIConvert(set, template, out PKM pk);
                if (success)
                {
                    msg = LegalizationResult.Regenerated;
                    return pk;
                }
                if (!AllowBruteForce)
                {
                    msg = LegalizationResult.Failed;
                    return pk;
                }
            }

            if (AllowBruteForce)
            {
                msg = LegalizationResult.BruteForce;
                return tr.GetBruteForcedLegalMon(set, template);
            }

            msg = LegalizationResult.Failed;
            return template;
        }

        /// <summary>
        /// API Legality
        /// </summary>
        /// <param name="tr">trainer data</param>
        /// <param name="set">showdown set to legalize from</param>
        /// <param name="template">pkm file to legalize</param>
        /// <param name="pkm">legalized pkm file</param>
        /// <returns>bool if the pokemon was legalized via API or bruteforce</returns>
        private static bool TryAPIConvert(this ITrainerInfo tr, IBattleTemplate set, PKM template, out PKM pkm)
        {
            pkm = tr.GetLegalFromTemplate(template, set, out bool satisfied);
            if (!satisfied)
                return false;

            var trainer = TrainerSettings.GetSavedTrainerData(pkm, tr);
            pkm.SetAllTrainerData(trainer);
            return true;
        }

        /// <summary>
        /// Method to bruteforce the pkm (won't be documented, because fuck bruteforce)
        /// </summary>
        /// <param name="tr">trainerdata</param>
        /// <param name="set">showdown set</param>
        /// <param name="template">template pkm to bruteforce</param>
        /// <returns>(Hopefully) Legalized pkm file</returns>
        private static PKM GetBruteForcedLegalMon(this ITrainerInfo tr, IBattleTemplate set, PKM template)
        {
            var resetForm = ShowdownUtil.IsInvalidForm(set.Form);
            var trainer = TrainerSettings.GetSavedTrainerData(template, tr);
            var legal = BruteForce.ApplyDetails(template, set, resetForm, trainer);
            legal.SetAllTrainerData(trainer);
            return legal;
        }

        /// <summary>
        /// Method to find all empty slots in a current box
        /// </summary>
        /// <param name="data">Box Data of the save file</param>
        /// <param name="start">Starting position for finding an empty slot</param>
        /// <returns>A list of all indices in the current box that are empty</returns>
        private static List<int> FindAllEmptySlots(IList<PKM> data, int start)
        {
            var emptySlots = new List<int>();
            for (int i = start; i < data.Count; i++)
            {
                if (data[i].Species < 1)
                    emptySlots.Add(i);
            }
            return emptySlots;
        }
    }
}
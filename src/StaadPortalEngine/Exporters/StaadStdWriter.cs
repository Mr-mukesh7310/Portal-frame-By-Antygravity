using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StaadPortalEngine.Generators;
using StaadPortalEngine.Models;

namespace StaadPortalEngine.Exporters
{
    public static class StaadStdWriter
    {
        public static string GenerateStdText(GeneratedModel model)
        {
            var sb = new StringBuilder();

            sb.AppendLine("STAAD SPACE");
            sb.AppendLine("START JOB INFORMATION");
            sb.AppendLine($"ENGINEER DATE {DateTime.Now:dd-MMM-yy}");
            sb.AppendLine($"JOB NAME {model.Configuration.ModelName}");
            sb.AppendLine("JOB CLIENT PARAMETRIC PORTAL GENERATOR");
            sb.AppendLine("END JOB INFORMATION");
            sb.AppendLine("INPUT WIDTH 79");
            sb.AppendLine("UNIT METER KN");

            // 1. Joint Coordinates
            sb.AppendLine("JOINT COORDINATES");
            foreach (var node in model.Nodes.Values.OrderBy(n => n.Id))
            {
                sb.AppendLine($"{node.Id} {node.X:F4} {node.Y:F4} {node.Z:F4};");
            }

            // 2. Member Incidences
            sb.AppendLine("MEMBER INCIDENCES");
            foreach (var beam in model.Beams.Values.OrderBy(b => b.Id))
            {
                sb.AppendLine($"{beam.Id} {beam.NodeA} {beam.NodeB};");
            }

            // 3. Group Definitions (Format: START GROUP DEFINITION / MEMBER / _GROUPNAME list / END GROUP DEFINITION)
            sb.AppendLine("START GROUP DEFINITION");
            sb.AppendLine("MEMBER");

            // Frame Group (All structural columns and rafters)
            var frameMemberIds = model.Beams.Values
                .Where(b => b.Type == MemberType.Column || b.Type == MemberType.IntermediateColumn || b.Type == MemberType.Rafter)
                .Select(b => b.Id).ToList();
            if (frameMemberIds.Any())
            {
                sb.AppendLine(FormatStaadLineList("_FRAME", frameMemberIds));
            }

            // Specific Member Type Groups
            var colIds = model.Beams.Values.Where(b => b.Type == MemberType.Column || b.Type == MemberType.IntermediateColumn).Select(b => b.Id).ToList();
            if (colIds.Any()) sb.AppendLine(FormatStaadLineList("_COLUMNS", colIds));

            var rafterIds = model.Beams.Values.Where(b => b.Type == MemberType.Rafter).Select(b => b.Id).ToList();
            if (rafterIds.Any()) sb.AppendLine(FormatStaadLineList("_RAFTERS", rafterIds));

            var strutIds = model.Beams.Values.Where(b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut || b.Type == MemberType.RccWallTieBeam).Select(b => b.Id).ToList();
            if (strutIds.Any()) sb.AppendLine(FormatStaadLineList("_STRUTS", strutIds));

            var bracingIds = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing || b.Type == MemberType.WallBracing).Select(b => b.Id).ToList();
            if (bracingIds.Any()) sb.AppendLine(FormatStaadLineList("_BRACING", bracingIds));

            var roofBraceIds = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing).Select(b => b.Id).ToList();
            if (roofBraceIds.Any()) sb.AppendLine(FormatStaadLineList("_ROOFBRACING", roofBraceIds));

            var wallBraceIds = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).Select(b => b.Id).ToList();
            if (wallBraceIds.Any()) sb.AppendLine(FormatStaadLineList("_WALLBRACING", wallBraceIds));

            var portalBeamIds = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam).Select(b => b.Id).ToList();
            if (portalBeamIds.Any()) sb.AppendLine(FormatStaadLineList("_PORTALBEAMS", portalBeamIds));

            var portalLegIds = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace).Select(b => b.Id).ToList();
            if (portalLegIds.Any()) sb.AppendLine(FormatStaadLineList("_PORTALLEGS", portalLegIds));

            var gableIds = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).Select(b => b.Id).ToList();
            if (gableIds.Any()) sb.AppendLine(FormatStaadLineList("_GABLEPOSTS", gableIds));

            var cableTrayIds = model.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket || b.Type == MemberType.CableTrayRunner).Select(b => b.Id).ToList();
            if (cableTrayIds.Any()) sb.AppendLine(FormatStaadLineList("_CABLETRAY", cableTrayIds));

            var mezIds = model.Beams.Values.Where(b => b.Type == MemberType.MezzanineMainBeam || b.Type == MemberType.MezzanineSecondaryBeam || b.Type == MemberType.MezzanineColumn).Select(b => b.Id).ToList();
            if (mezIds.Any()) sb.AppendLine(FormatStaadLineList("_MEZZANINE", mezIds));

            var jackBeamIds = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).Select(b => b.Id).ToList();
            if (jackBeamIds.Any()) sb.AppendLine(FormatStaadLineList("_JACKBEAMS", jackBeamIds));

            var stubIds = model.Beams.Values.Where(b => b.GroupName == "JACK_POST").Select(b => b.Id).ToList();
            if (stubIds.Any()) sb.AppendLine(FormatStaadLineList("_JACKPOSTS", stubIds));

            sb.AppendLine("END GROUP DEFINITION");

            // 4. Material Definition
            sb.AppendLine("DEFINE MATERIAL START");
            sb.AppendLine("ISOTROPIC STEEL");
            sb.AppendLine("E 2.05e+08");
            sb.AppendLine("POISSON 0.3");
            sb.AppendLine("DENSITY 76.8195");
            sb.AppendLine("ALPHA 1.2e-05");
            sb.AppendLine("DAMP 0.03");
            sb.AppendLine("G 7.88462e+07");
            sb.AppendLine("TYPE STEEL");
            sb.AppendLine("STRENGTH RY 1.5 RT 1.2");
            sb.AppendLine("END DEFINE MATERIAL");

            // 5. Member Property Assignment Placeholders
            sb.AppendLine("MEMBER PROPERTY INDIAN");
            AssignPropertiesByGroup(sb, model);

            sb.AppendLine("CONSTANTS");
            if (model.Beta90BeamIds.Any())
            {
                sb.AppendLine(FormatStaadLineList("BETA 90 MEMB", model.Beta90BeamIds));
            }
            sb.AppendLine("MATERIAL STEEL ALL");

            // 6. Member Specifications (Format: ******MEMBER SPECIFICATION / MEMBER TENSION / list / MEMBER TRUSS / list / MEMBER RELEASE / list START MY MZ / list END MY MZ)
            if (model.TensionOnlyBeamIds.Any() || model.PinnedEndBeamIds.Any() || model.StartReleaseMyMzBeamIds.Any() || model.EndReleaseMyMzBeamIds.Any())
            {
                sb.AppendLine("******MEMBER SPECIFICATION");
                if (model.TensionOnlyBeamIds.Any())
                {
                    sb.AppendLine("MEMBER TENSION");
                    sb.AppendLine(FormatStaadLineList("", model.TensionOnlyBeamIds));
                }

                if (model.PinnedEndBeamIds.Any())
                {
                    sb.AppendLine("MEMBER TRUSS");
                    sb.AppendLine(FormatStaadLineList("", model.PinnedEndBeamIds));
                }

                if (model.StartReleaseMyMzBeamIds.Any() || model.EndReleaseMyMzBeamIds.Any())
                {
                    sb.AppendLine("MEMBER RELEASE");
                    if (model.StartReleaseMyMzBeamIds.Any())
                    {
                        sb.AppendLine(FormatStaadLineList("", model.StartReleaseMyMzBeamIds, suffix: "START MY MZ"));
                    }

                    if (model.EndReleaseMyMzBeamIds.Any())
                    {
                        sb.AppendLine(FormatStaadLineList("", model.EndReleaseMyMzBeamIds, suffix: "END MY MZ"));
                    }
                }
            }

            // 7. Supports (Side wall columns = FIXED, Intermediate & Gable columns = PINNED)
            if (model.FixedBaseNodeIds.Any() || model.PinnedBaseNodeIds.Any() || model.BaseNodeIds.Any())
            {
                sb.AppendLine("SUPPORTS");
                if (model.FixedBaseNodeIds.Any())
                {
                    sb.AppendLine(FormatStaadLineList("", model.FixedBaseNodeIds) + " FIXED");
                }
                if (model.PinnedBaseNodeIds.Any())
                {
                    sb.AppendLine(FormatStaadLineList("", model.PinnedBaseNodeIds) + " PINNED");
                }
                if (!model.FixedBaseNodeIds.Any() && !model.PinnedBaseNodeIds.Any() && model.BaseNodeIds.Any())
                {
                    sb.AppendLine(FormatStaadLineList("", model.BaseNodeIds) + " PINNED");
                }
            }

            // 8. Basic Loading Placeholders
            sb.AppendLine("LOADING 1 LOADTYPE Dead TITLE DEAD LOAD");
            sb.AppendLine("SELFWEIGHT Y -1.0");

            sb.AppendLine("LOADING 2 LOADTYPE Live TITLE ROOF LIVE LOAD");
            sb.AppendLine("LOADING 3 LOADTYPE Wind TITLE WIND LOAD +X");
            sb.AppendLine("LOADING 4 LOADTYPE Wind TITLE WIND LOAD +Z");

            sb.AppendLine("PERFORM ANALYSIS PRINT ALL");
            sb.AppendLine("FINISH");

            return sb.ToString();
        }

        public static void WriteToFile(string filePath, GeneratedModel model)
        {
            var text = GenerateStdText(model);
            File.WriteAllText(filePath, text);
        }

        private static void AssignPropertiesByGroup(StringBuilder sb, GeneratedModel model)
        {
            var colBeams = model.Beams.Values.Where(b => (b.Type == MemberType.Column || b.Type == MemberType.IntermediateColumn) && b.GroupName != "JACK_POST").Select(b => b.Id).ToList();
            var stubBeams = model.Beams.Values.Where(b => b.GroupName == "JACK_POST").Select(b => b.Id).ToList();
            var rafBeams = model.Beams.Values.Where(b => b.Type == MemberType.Rafter).Select(b => b.Id).ToList();
            var strutBeams = model.Beams.Values.Where(b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut || b.Type == MemberType.RccWallTieBeam).Select(b => b.Id).ToList();
            var brkBeams = model.Beams.Values.Where(b => b.Type == MemberType.RoofBracing || b.Type == MemberType.WallBracing).Select(b => b.Id).ToList();
            var gableBeams = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).Select(b => b.Id).ToList();
            var cableTrayBeams = model.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket || b.Type == MemberType.CableTrayRunner).Select(b => b.Id).ToList();
            var mezBeams = model.Beams.Values.Where(b => b.Type == MemberType.MezzanineMainBeam || b.Type == MemberType.MezzanineSecondaryBeam || b.Type == MemberType.MezzanineColumn).Select(b => b.Id).ToList();
            var portalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam).Select(b => b.Id).ToList();
            var kneeBraceBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace).Select(b => b.Id).ToList();
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).Select(b => b.Id).ToList();

            if (colBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", colBeams)} TABLE ST ISMB450");
            if (rafBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", rafBeams)} TABLE ST ISMB400");
            if (strutBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", strutBeams)} TABLE ST ISMC150");
            if (brkBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", brkBeams)} TABLE ST ISA65X65X6");
            if (gableBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", gableBeams)} TABLE ST ISMB250");
            if (cableTrayBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", cableTrayBeams)} TABLE ST ISMC100");
            if (mezBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", mezBeams)} TABLE ST ISMB300");
            if (portalBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", portalBeams)} TABLE ST ISMB350");
            if (kneeBraceBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", kneeBraceBeams)} TABLE ST ISMC150");
            if (jackBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", jackBeams)} TABLE ST ISMB500");
            if (stubBeams.Any()) sb.AppendLine($"{FormatStaadLineList("", stubBeams)} ASSIGN COLUMN");
        }

        public static List<string> CompressToStaadRanges(IEnumerable<int> ids)
        {
            var sorted = ids.Distinct().OrderBy(x => x).ToList();
            var tokens = new List<string>();
            if (sorted.Count == 0) return tokens;

            int start = sorted[0];
            int prev = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                int curr = sorted[i];
                if (curr == prev + 1)
                {
                    prev = curr;
                }
                else
                {
                    if (prev == start)
                    {
                        tokens.Add(start.ToString());
                    }
                    else
                    {
                        tokens.Add($"{start} TO {prev}");
                    }
                    start = curr;
                    prev = curr;
                }
            }

            if (prev == start)
            {
                tokens.Add(start.ToString());
            }
            else
            {
                tokens.Add($"{start} TO {prev}");
            }

            return tokens;
        }

        public static string FormatStaadLineList(string header, IEnumerable<int> ids, int maxLineLength = 72, string indent = "", string suffix = "")
        {
            var tokens = CompressToStaadRanges(ids);
            if (tokens.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            string currentLine = string.IsNullOrWhiteSpace(header) ? indent : header;

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                string candidate = (currentLine.Length == 0 || currentLine.EndsWith(" ") || currentLine.EndsWith("\t"))
                    ? currentLine + token
                    : currentLine + " " + token;

                int extraLen = (i == tokens.Count - 1 && !string.IsNullOrEmpty(suffix)) ? suffix.Length + 1 : 0;

                if (candidate.Length + extraLen > maxLineLength && currentLine.Trim().Length > 0)
                {
                    sb.AppendLine(currentLine + " -");
                    currentLine = indent + token;
                }
                else
                {
                    currentLine = candidate;
                }
            }

            if (!string.IsNullOrEmpty(suffix))
            {
                currentLine += (currentLine.Length > 0 ? " " : "") + suffix;
            }

            if (!string.IsNullOrWhiteSpace(currentLine))
            {
                sb.AppendLine(currentLine);
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}

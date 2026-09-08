using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using StaadPortalEngine.Models;

namespace StaadPortalEngine.Parsers
{
    public static class StaadStdParser
    {
        public static GeneratedModel Parse(string stdText)
        {
            var model = new GeneratedModel();
            if (string.IsNullOrWhiteSpace(stdText))
                return model;

            var rawLines = stdText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanLines = new List<string>();

            // Pre-process: merge continuation lines (ending in -) and strip comments (*)
            for (int i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i].Trim();
                if (line.StartsWith("*") || string.IsNullOrWhiteSpace(line)) continue;

                // Handle comment after text (e.g. "JOINT COORDINATES * comment")
                int commentIdx = line.IndexOf('*');
                if (commentIdx >= 0)
                {
                    line = line.Substring(0, commentIdx).Trim();
                }

                while (line.EndsWith("-") && i + 1 < rawLines.Length)
                {
                    line = line.Substring(0, line.Length - 1).Trim() + " " + rawLines[++i].Trim();
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    cleanLines.Add(line);
                }
            }

            string currentSection = "NONE";
            var customPropsBuilder = new System.Text.StringBuilder();
            var loadLines = new List<string>();

            for (int lineIndex = 0; lineIndex < cleanLines.Count; lineIndex++)
            {
                var line = cleanLines[lineIndex];
                var upper = line.ToUpperInvariant();

                // Section Headers
                if (upper.StartsWith("JOINT COORDINATES") || upper.StartsWith("COORDINATES"))
                {
                    currentSection = "JOINTS";
                    continue;
                }
                else if (upper.StartsWith("MEMBER INCIDENCES") || upper.StartsWith("INCIDENCES"))
                {
                    currentSection = "INCIDENCES";
                    continue;
                }
                else if (upper.StartsWith("MEMBER PROPERTY") || upper.StartsWith("ELEMENT PROPERTY"))
                {
                    currentSection = "MEMBER_PROPERTY";
                    customPropsBuilder.AppendLine(line);
                    ParseMemberPropertyLine(line, model);
                    continue;
                }
                else if (upper.StartsWith("MEMBER RELEASE") || upper.StartsWith("RELEASE"))
                {
                    currentSection = "RELEASES";
                    continue;
                }
                else if (upper.StartsWith("SUPPORTS") || upper.StartsWith("SUPPORT"))
                {
                    currentSection = "SUPPORTS";
                    continue;
                }
                else if (upper.StartsWith("MEMBER TENSION") || upper.StartsWith("TENSION"))
                {
                    currentSection = "TENSION";
                    continue;
                }
                else if (upper.StartsWith("CONSTANTS") || upper.StartsWith("MATERIAL"))
                {
                    currentSection = "CONSTANTS";
                    continue;
                }
                else if (upper.StartsWith("DEFINE ") || upper.StartsWith("CUT OFF") ||
                         upper.StartsWith("LOAD ") || upper.StartsWith("LOAD\t") || upper.StartsWith("LOADING ") || upper.StartsWith("REPEAT LOAD") ||
                         upper.StartsWith("PARAMETER ") || upper.StartsWith("CODE ") || upper.StartsWith("CHECK CODE") ||
                         upper.StartsWith("SELECT ") || upper.StartsWith("PERFORM ") || upper.StartsWith("PRINT ") ||
                         upper.StartsWith("CHANGE") || upper.StartsWith("STEEL TAKE OFF") ||
                         upper.StartsWith("START CONCRETE") || upper.StartsWith("END CONCRETE") ||
                         upper.StartsWith("START USER") || upper.StartsWith("FINISH"))
                {
                    currentSection = "POST_STRUCTURE_COMMANDS";
                    loadLines.Add(line);
                    continue;
                }

                // Section Processing
                if (currentSection == "JOINTS")
                {
                    ParseJointLine(line, model);
                }
                else if (currentSection == "INCIDENCES")
                {
                    ParseIncidenceLine(line, model);
                }
                else if (currentSection == "MEMBER_PROPERTY")
                {
                    customPropsBuilder.AppendLine(line);
                    ParseMemberPropertyLine(line, model);
                }
                else if (currentSection == "RELEASES")
                {
                    ParseReleaseLine(line, model);
                }
                else if (currentSection == "SUPPORTS")
                {
                    if (upper.Contains("FIXED") || upper.Contains("PINNED") || upper.Contains("SPRING") || upper.Contains("GENERATE"))
                    {
                        ParseSupportLine(line, model);
                    }
                    else
                    {
                        currentSection = "POST_STRUCTURE_COMMANDS";
                        loadLines.Add(line);
                    }
                }
                else if (currentSection == "TENSION")
                {
                    ParseTensionLine(line, model);
                }
                else if (currentSection == "CONSTANTS" || upper.Contains("BETA 90") || upper.Contains("BETA 0"))
                {
                    ParseBetaLine(line, model);
                }
                else if (currentSection == "POST_STRUCTURE_COMMANDS")
                {
                    loadLines.Add(line);
                }
            }

            model.CustomPropertyText = customPropsBuilder.ToString().Trim();

            // Infer Member Types for visual 3D color coding
            InferMemberTypes(model);

            // Construct Template2DFrameDefinition
            var template = new Template2DFrameDefinition();
            foreach (var n in model.Nodes.Values)
            {
                template.Nodes.Add(new Template2DNode
                {
                    Id = n.Id,
                    X = n.X,
                    Y = n.Y,
                    Z = n.Z,
                    IsBase = model.FixedBaseNodeIds.Contains(n.Id) || model.PinnedBaseNodeIds.Contains(n.Id) || model.BaseNodeIds.Contains(n.Id) || n.Y <= 0.25,
                    SupportType = model.FixedBaseNodeIds.Contains(n.Id) ? "FIXED" : (model.PinnedBaseNodeIds.Contains(n.Id) ? "PINNED" : (n.Y <= 0.25 ? "PINNED" : ""))
                });
            }

            foreach (var b in model.Beams.Values)
            {
                template.Beams.Add(new Template2DBeam
                {
                    Id = b.Id,
                    NodeA = b.NodeA,
                    NodeB = b.NodeB,
                    Type = b.Type,
                    GroupName = b.GroupName,
                    SectionProperty = b.SectionProperty,
                    StartReleaseMyMz = model.StartReleaseMyMzBeamIds.Contains(b.Id),
                    EndReleaseMyMz = model.EndReleaseMyMzBeamIds.Contains(b.Id),
                    Beta90 = model.Beta90BeamIds.Contains(b.Id)
                });
            }

            template.LoadLines = loadLines;
            model.TemplateFrame = template;

            return model;
        }

        private static void ParseMemberPropertyLine(string line, GeneratedModel model)
        {
            var clean = line.Trim();
            if (string.IsNullOrWhiteSpace(clean)) return;
            if (clean.EndsWith(";")) clean = clean.Substring(0, clean.Length - 1).Trim();

            var upper = clean.ToUpperInvariant();

            string[] propKeywords = new[] { "TAPERED", "TABLE", "PRIS", "PRISMATIC", "UPT", "ASSIGN", "TUB", "PIPE" };
            int keywordIndex = -1;
            foreach (var kw in propKeywords)
            {
                int idx = upper.IndexOf(kw);
                if (idx > 0 && (keywordIndex == -1 || idx < keywordIndex))
                {
                    keywordIndex = idx;
                }
            }

            if (keywordIndex > 0)
            {
                string idPart = clean.Substring(0, keywordIndex).Trim();
                string propPart = clean.Substring(keywordIndex).Trim();

                var idUpper = idPart.ToUpperInvariant();
                if (idUpper.StartsWith("MEMBER PROPERTY"))
                {
                    idPart = idPart.Substring(15).Trim();
                    idUpper = idPart.ToUpperInvariant();
                }
                if (idUpper.StartsWith("INDIAN") || idUpper.StartsWith("AMERICAN") || idUpper.StartsWith("COLDFORMED") || idUpper.StartsWith("BRITISH") || idUpper.StartsWith("EUROPEAN"))
                {
                    int spaceIdx = idPart.IndexOf(' ');
                    if (spaceIdx > 0) idPart = idPart.Substring(spaceIdx).Trim();
                    else idPart = "";
                }

                var ids = ParseIdList(idPart);
                foreach (var id in ids)
                {
                    if (model.Beams.TryGetValue(id, out var beam))
                    {
                        beam.SectionProperty = propPart;
                    }
                }
            }
        }

        private static void ParseJointLine(string line, GeneratedModel model)
        {
            var statements = line.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var stmt in statements)
            {
                var tokens = stmt.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 4)
                {
                    if (int.TryParse(tokens[0], out int id) &&
                        double.TryParse(tokens[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(tokens[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double y) &&
                        double.TryParse(tokens[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double z))
                    {
                        if (!model.Nodes.ContainsKey(id))
                        {
                            model.Nodes[id] = new Node3D(id, x, y, z);
                        }
                    }
                }
            }
        }

        private static void ParseIncidenceLine(string line, GeneratedModel model)
        {
            var statements = line.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var stmt in statements)
            {
                var tokens = stmt.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 3)
                {
                    if (int.TryParse(tokens[0], out int beamId) &&
                        int.TryParse(tokens[1], out int nodeA) &&
                        int.TryParse(tokens[2], out int nodeB))
                    {
                        if (!model.Beams.ContainsKey(beamId))
                        {
                            model.Beams[beamId] = new Beam3D(beamId, nodeA, nodeB, MemberType.Column);
                        }
                    }
                }
                else if (tokens.Length > 3)
                {
                    // Could be multiple tuples e.g. "1 1 2 2 2 3 3 3 4"
                    int idx = 0;
                    while (idx + 2 < tokens.Length)
                    {
                        if (int.TryParse(tokens[idx], out int beamId) &&
                            int.TryParse(tokens[idx + 1], out int nodeA) &&
                            int.TryParse(tokens[idx + 2], out int nodeB))
                        {
                            if (!model.Beams.ContainsKey(beamId))
                            {
                                model.Beams[beamId] = new Beam3D(beamId, nodeA, nodeB, MemberType.Column);
                            }
                            idx += 3;
                        }
                        else
                        {
                            idx++;
                        }
                    }
                }
            }
        }

        private static void ParseReleaseLine(string line, GeneratedModel model)
        {
            var upper = line.ToUpperInvariant();
            var tokens = line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

            bool isStart = upper.Contains("START");
            bool isEnd = upper.Contains("END");
            bool hasMyMz = upper.Contains("MY") || upper.Contains("MZ") || upper.Contains("MP") || upper.Contains("KFX");

            if (!isStart && !isEnd) return;

            var beamIds = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i].ToUpperInvariant();
                if (t == "START" || t == "END" || t == "MY" || t == "MZ" || t == "FX" || t == "FY" || t == "FZ" || t == "MX") break;

                if (t == "TO" && i > 0 && i + 1 < tokens.Length)
                {
                    if (int.TryParse(tokens[i - 1], out int fromId) && int.TryParse(tokens[i + 1], out int toId))
                    {
                        for (int id = fromId + 1; id <= toId; id++)
                        {
                            beamIds.Add(id);
                        }
                    }
                    i++;
                }
                else if (int.TryParse(t, out int bId))
                {
                    beamIds.Add(bId);
                }
            }

            foreach (var bId in beamIds)
            {
                if (isStart && !model.StartReleaseMyMzBeamIds.Contains(bId))
                    model.StartReleaseMyMzBeamIds.Add(bId);

                if (isEnd && !model.EndReleaseMyMzBeamIds.Contains(bId))
                    model.EndReleaseMyMzBeamIds.Add(bId);
            }
        }

        private static void ParseSupportLine(string line, GeneratedModel model)
        {
            var upper = line.ToUpperInvariant();
            var tokens = line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

            bool isFixed = upper.Contains("FIXED");
            bool isPinned = upper.Contains("PINNED");

            if (!isFixed && !isPinned) return;

            var nodeIds = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i].ToUpperInvariant();
                if (t == "FIXED" || t == "PINNED" || t == "GEN") break;

                if (t == "TO" && i > 0 && i + 1 < tokens.Length)
                {
                    if (int.TryParse(tokens[i - 1], out int fromId) && int.TryParse(tokens[i + 1], out int toId))
                    {
                        for (int id = fromId + 1; id <= toId; id++)
                        {
                            nodeIds.Add(id);
                        }
                    }
                    i++;
                }
                else if (int.TryParse(t, out int nId))
                {
                    nodeIds.Add(nId);
                }
            }

            foreach (var nId in nodeIds)
            {
                if (!model.BaseNodeIds.Contains(nId)) model.BaseNodeIds.Add(nId);
                if (isFixed && !model.FixedBaseNodeIds.Contains(nId)) model.FixedBaseNodeIds.Add(nId);
                if (isPinned && !model.PinnedBaseNodeIds.Contains(nId)) model.PinnedBaseNodeIds.Add(nId);
            }
        }

        private static void ParseTensionLine(string line, GeneratedModel model)
        {
            var tokens = line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i].ToUpperInvariant();
                if (t == "MEMBER" || t == "TENSION") continue;

                if (t == "TO" && i > 0 && i + 1 < tokens.Length)
                {
                    if (int.TryParse(tokens[i - 1], out int fromId) && int.TryParse(tokens[i + 1], out int toId))
                    {
                        for (int id = fromId + 1; id <= toId; id++)
                        {
                            if (!model.TensionOnlyBeamIds.Contains(id)) model.TensionOnlyBeamIds.Add(id);
                        }
                    }
                    i++;
                }
                else if (int.TryParse(t, out int bId))
                {
                    if (!model.TensionOnlyBeamIds.Contains(bId)) model.TensionOnlyBeamIds.Add(bId);
                }
            }
        }

        private static void ParseBetaLine(string line, GeneratedModel model)
        {
            var upper = line.ToUpperInvariant();
            if (!upper.Contains("BETA 90")) return;

            var tokens = line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool afterMemb = false;

            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i].ToUpperInvariant();
                if (t == "MEMB" || t == "MEMBER" || t == "MEMBERS")
                {
                    afterMemb = true;
                    continue;
                }

                if (afterMemb)
                {
                    if (t == "TO" && i > 0 && i + 1 < tokens.Length)
                    {
                        if (int.TryParse(tokens[i - 1], out int fromId) && int.TryParse(tokens[i + 1], out int toId))
                        {
                            for (int id = fromId + 1; id <= toId; id++)
                            {
                                if (!model.Beta90BeamIds.Contains(id)) model.Beta90BeamIds.Add(id);
                            }
                        }
                        i++;
                    }
                    else if (int.TryParse(t, out int bId))
                    {
                        if (!model.Beta90BeamIds.Contains(bId)) model.Beta90BeamIds.Add(bId);
                    }
                }
            }
        }

        private static void InferMemberTypes(GeneratedModel model)
        {
            if (model.Nodes.Count == 0 || model.Beams.Count == 0) return;

            var baseNodes = model.Nodes.Values.Where(n => n.Y <= 0.25 || model.BaseNodeIds.Contains(n.Id)).ToList();
            double minX = baseNodes.Count > 0 ? baseNodes.Min(n => n.X) : model.Nodes.Values.Min(n => n.X);
            double maxX = baseNodes.Count > 0 ? baseNodes.Max(n => n.X) : model.Nodes.Values.Max(n => n.X);
            double minY = model.Nodes.Values.Min(n => n.Y);
            double maxY = model.Nodes.Values.Max(n => n.Y);
            double minZ = model.Nodes.Values.Min(n => n.Z);
            double maxZ = model.Nodes.Values.Max(n => n.Z);

            foreach (var beam in model.Beams.Values)
            {
                if (!model.Nodes.TryGetValue(beam.NodeA, out var nA) || !model.Nodes.TryGetValue(beam.NodeB, out var nB))
                    continue;

                double dx = Math.Abs(nA.X - nB.X);
                double dy = Math.Abs(nA.Y - nB.Y);
                double dz = Math.Abs(nA.Z - nB.Z);

                // Tension X-Bracings
                if (model.TensionOnlyBeamIds.Contains(beam.Id))
                {
                    beam.Type = (Math.Max(nA.Y, nB.Y) >= maxY * 0.7) ? MemberType.RoofBracing : MemberType.WallBracing;
                    continue;
                }

                // Vertical Members (Columns, Gable Posts, Intermediate Columns)
                if (dx < 0.05 && dz < 0.05 && dy > 0.1)
                {
                    double xVal = (nA.X + nB.X) / 2.0;
                    double zVal = (nA.Z + nB.Z) / 2.0;

                    if (Math.Abs(xVal - minX) < 0.1 || Math.Abs(xVal - maxX) < 0.1)
                    {
                        beam.Type = MemberType.Column; // Side exterior column
                    }
                    else if (Math.Abs(maxZ - minZ) > 0.5 && (Math.Abs(zVal - minZ) < 0.1 || Math.Abs(zVal - maxZ) < 0.1))
                    {
                        beam.Type = MemberType.GablePost; // Front / Rear Gable Post in 3D model
                    }
                    else
                    {
                        beam.Type = MemberType.IntermediateColumn; // Interior intermediate column
                    }
                    continue;
                }

                // Longitudinal Members (Eave struts, Ridge struts, Jack beams)
                if (dx < 0.05 && dy < 0.05 && dz > 0.1)
                {
                    double yVal = (nA.Y + nB.Y) / 2.0;
                    double xVal = (nA.X + nB.X) / 2.0;

                    if (Math.Abs(yVal - maxY) < 0.3)
                    {
                        beam.Type = MemberType.RidgeStrut;
                    }
                    else if (Math.Abs(xVal - minX) < 0.1 || Math.Abs(xVal - maxX) < 0.1)
                    {
                        beam.Type = MemberType.EaveStrut;
                    }
                    else if (model.Beta90BeamIds.Contains(beam.Id) || yVal > maxY * 0.5)
                    {
                        beam.Type = MemberType.JackBeam;
                    }
                    else
                    {
                        beam.Type = MemberType.EaveStrut;
                    }
                    continue;
                }

                // Transverse Sloped Members (Rafters)
                if (dz < 0.05 && dy > 0.01 && (nA.Y > minY + 2.0 || nB.Y > minY + 2.0))
                {
                    beam.Type = MemberType.Rafter;
                    continue;
                }

                // Transverse Horizontal Members (Cable tray brackets or Mezzanine beams)
                if (dz < 0.05 && dy < 0.05 && dx > 0.1)
                {
                    if (dx < 2.0)
                    {
                        beam.Type = MemberType.CableTrayBracket;
                    }
                    else
                    {
                        beam.Type = MemberType.MezzanineMainBeam;
                    }
                    continue;
                }

                // Diagonal Members (Bracings)
                if (dz > 0.1 && (dx > 0.1 || dy > 0.1))
                {
                    beam.Type = (Math.Max(nA.Y, nB.Y) >= maxY * 0.7) ? MemberType.RoofBracing : MemberType.WallBracing;
                    continue;
                }

                // Default
                beam.Type = MemberType.Rafter;
            }
        }

        public static ExtractedPortalParameters ExtractParameters(GeneratedModel model)
        {
            var result = new ExtractedPortalParameters();
            if (model == null || model.Nodes.Count == 0)
                return result;

            var baseNodes = model.Nodes.Values.Where(n => n.Y <= 0.25 || model.BaseNodeIds.Contains(n.Id)).ToList();
            double minX = baseNodes.Count > 0 ? baseNodes.Min(n => n.X) : model.Nodes.Values.Min(n => n.X);
            double maxX = baseNodes.Count > 0 ? baseNodes.Max(n => n.X) : model.Nodes.Values.Max(n => n.X);
            double spanWidth = Math.Round(maxX - minX, 2);
            if (spanWidth < 0.1) spanWidth = 24.0;
            result.SpanWidth = spanWidth;

            // 1. Eave Height: Highest node at left (X ~ minX) and right (X ~ maxX) boundaries
            var leftNodes = model.Nodes.Values.Where(n => Math.Abs(n.X - minX) < 0.15).ToList();
            var rightNodes = model.Nodes.Values.Where(n => Math.Abs(n.X - maxX) < 0.15).ToList();

            double eaveLeft = leftNodes.Count > 0 ? leftNodes.Max(n => n.Y) : 7.0;
            double eaveRight = rightNodes.Count > 0 ? rightNodes.Max(n => n.Y) : 7.0;
            double eaveHeight = Math.Round(Math.Min(eaveLeft, eaveRight), 2);
            if (eaveHeight < 1.0) eaveHeight = 7.0;
            result.EaveHeight = eaveHeight;

            // 2. Ridge Height & Roof Slope (1 in N)
            double maxY = model.Nodes.Values.Max(n => n.Y);
            double ridgeHeight = Math.Round(maxY, 2);
            result.RidgeHeight = ridgeHeight;

            double halfSpan = spanWidth / 2.0;
            double rise = Math.Max(0.0, ridgeHeight - eaveHeight);
            double slopeRatio = 10.0;
            if (rise > 0.05)
            {
                slopeRatio = Math.Round(halfSpan / rise, 1);
                if (slopeRatio < 1.0) slopeRatio = 1.0;
            }
            result.RoofSlopeRatio = slopeRatio;

            // 3. Intermediate Columns / Width Modules (X)
            var intColXList = new List<double>();
            foreach (var node in model.Nodes.Values)
            {
                if (Math.Abs(node.Y) < 0.25 && node.X > minX + 0.25 && node.X < maxX - 0.25)
                {
                    double relX = Math.Round(node.X - minX, 2);
                    if (!intColXList.Any(x => Math.Abs(x - relX) < 0.15))
                    {
                        intColXList.Add(relX);
                    }
                }
            }

            if (intColXList.Count == 0)
            {
                foreach (var beam in model.Beams.Values)
                {
                    if (model.Nodes.TryGetValue(beam.NodeA, out var nA) && model.Nodes.TryGetValue(beam.NodeB, out var nB))
                    {
                        double midX = (nA.X + nB.X) / 2.0;
                        if (Math.Abs(nA.X - nB.X) < 0.1 && Math.Abs(nA.Z - nB.Z) < 0.1 && Math.Abs(nA.Y - nB.Y) > 1.0)
                        {
                            if (midX > minX + 0.25 && midX < maxX - 0.25)
                            {
                                double relX = Math.Round(midX - minX, 2);
                                if (!intColXList.Any(x => Math.Abs(x - relX) < 0.15))
                                {
                                    intColXList.Add(relX);
                                }
                            }
                        }
                    }
                }
            }

            intColXList.Sort();
            result.IntermediateColumnOffsets = intColXList;

            if (intColXList.Count > 0)
            {
                var offsetsWithBounds = new List<double> { 0.0 };
                offsetsWithBounds.AddRange(intColXList);
                offsetsWithBounds.Add(spanWidth);

                var segmentSpacings = new List<double>();
                for (int i = 0; i < offsetsWithBounds.Count - 1; i++)
                {
                    segmentSpacings.Add(Math.Round(offsetsWithBounds[i + 1] - offsetsWithBounds[i], 2));
                }

                result.WidthModuleExpression = CompressSpacings(segmentSpacings);
            }
            else
            {
                result.WidthModuleExpression = "";
            }

            // 4. Longitudinal Bay Spacing Expression (Z)
            var distinctZList = new List<double>();
            foreach (var node in model.Nodes.Values)
            {
                double z = Math.Round(node.Z, 2);
                if (!distinctZList.Any(dz => Math.Abs(dz - z) < 0.15))
                {
                    distinctZList.Add(z);
                }
            }
            distinctZList = distinctZList.OrderByDescending(z => z).ToList();
            result.TotalFramesDetected = distinctZList.Count;

            if (distinctZList.Count > 1)
            {
                var baySpacings = new List<double>();
                for (int i = 0; i < distinctZList.Count - 1; i++)
                {
                    baySpacings.Add(Math.Round(distinctZList[i] - distinctZList[i + 1], 2));
                }
                result.BaySpacings = baySpacings;
                result.BaySpacingExpression = CompressSpacings(baySpacings);
            }
            else
            {
                result.BaySpacingExpression = "0";
            }

            // 5. RCC Wall Height (1st node above base on exterior columns)
            var exteriorWallNodes = model.Nodes.Values
                .Where(n => (Math.Abs(n.X - minX) < 0.15 || Math.Abs(n.X - maxX) < 0.15) && n.Y > 0.25 && n.Y < eaveHeight - 0.5)
                .ToList();
            if (exteriorWallNodes.Count > 0)
            {
                result.RccWallHeight = Math.Round(exteriorWallNodes.Min(n => n.Y), 2);
            }
            else
            {
                result.RccWallHeight = 0.0;
            }

            // 6. Canopy Detection (Cantilever nodes beyond minX or maxX)
            var leftCanopyNodes = model.Nodes.Values.Where(n => n.X < minX - 0.2).ToList();
            if (leftCanopyNodes.Count > 0)
            {
                result.HasLeftCanopy = true;
                var canopyBeams = model.Beams.Values.Where(b =>
                    (leftCanopyNodes.Any(cn => cn.Id == b.NodeA) && model.Nodes.TryGetValue(b.NodeB, out var nb) && Math.Abs(nb.X - minX) < 0.35) ||
                    (leftCanopyNodes.Any(cn => cn.Id == b.NodeB) && model.Nodes.TryGetValue(b.NodeA, out var na) && Math.Abs(na.X - minX) < 0.35)).ToList();
                if (canopyBeams.Count > 0)
                {
                    double cY = canopyBeams.Select(b => leftCanopyNodes.Any(cn => cn.Id == b.NodeA) ? model.Nodes[b.NodeB].Y : model.Nodes[b.NodeA].Y).Min();
                    result.LeftCanopyHeight = Math.Round(cY, 2);
                }
                else
                {
                    result.LeftCanopyHeight = Math.Round(leftCanopyNodes.Min(n => n.Y), 2);
                }
            }

            var rightCanopyNodes = model.Nodes.Values.Where(n => n.X > maxX + 0.15).ToList();
            if (rightCanopyNodes.Count > 0)
            {
                result.HasRightCanopy = true;
                var canopyBeams = model.Beams.Values.Where(b =>
                    (rightCanopyNodes.Any(cn => cn.Id == b.NodeA) && model.Nodes.TryGetValue(b.NodeB, out var nb) && Math.Abs(nb.X - maxX) < 0.35) ||
                    (rightCanopyNodes.Any(cn => cn.Id == b.NodeB) && model.Nodes.TryGetValue(b.NodeA, out var na) && Math.Abs(na.X - maxX) < 0.35)).ToList();
                if (canopyBeams.Count > 0)
                {
                    double cY = canopyBeams.Select(b => rightCanopyNodes.Any(cn => cn.Id == b.NodeA) ? model.Nodes[b.NodeB].Y : model.Nodes[b.NodeA].Y).Min();
                    result.RightCanopyHeight = Math.Round(cY, 2);
                }
                else
                {
                    result.RightCanopyHeight = Math.Round(rightCanopyNodes.Min(n => n.Y), 2);
                }
            }

            result.Template2DFrame = model.TemplateFrame;
            return result;
        }

        public static List<int> ParseIdList(string text)
        {
            var ids = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return ids;
            var tokens = text.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i].ToUpperInvariant();
                if (t == "TO" && i > 0 && i + 1 < tokens.Length)
                {
                    if (int.TryParse(tokens[i - 1], out int fromId) && int.TryParse(tokens[i + 1], out int toId))
                    {
                        for (int id = fromId + 1; id <= toId; id++)
                        {
                            ids.Add(id);
                        }
                    }
                    i++;
                }
                else if (int.TryParse(t, out int idVal))
                {
                    ids.Add(idVal);
                }
            }
            return ids;
        }

        public static string CompressSpacings(List<double> list)
        {
            if (list == null || list.Count == 0) return "0";
            var parts = new List<string>();
            int i = 0;
            while (i < list.Count)
            {
                double val = list[i];
                int count = 1;
                while (i + 1 < list.Count && Math.Abs(list[i + 1] - val) < 0.05)
                {
                    count++;
                    i++;
                }
                parts.Add($"{count}@{val:0.##}");
                i++;
            }
            return string.Join("+", parts);
        }
    }

    public class ExtractedPortalParameters
    {
        public double SpanWidth { get; set; } = 24.0;
        public double EaveHeight { get; set; } = 7.0;
        public double RidgeHeight { get; set; } = 8.2;
        public double RoofSlopeRatio { get; set; } = 10.0;
        public string WidthModuleExpression { get; set; } = "";
        public List<double> IntermediateColumnOffsets { get; set; } = new();
        public string BaySpacingExpression { get; set; } = "0";
        public List<double> BaySpacings { get; set; } = new();
        public double RccWallHeight { get; set; } = 0.0;
        public bool HasLeftCanopy { get; set; } = false;
        public double LeftCanopyHeight { get; set; } = 0.0;
        public bool HasRightCanopy { get; set; } = false;
        public double RightCanopyHeight { get; set; } = 0.0;
        public int TotalFramesDetected { get; set; } = 1;
        public Template2DFrameDefinition? Template2DFrame { get; set; }
    }
}

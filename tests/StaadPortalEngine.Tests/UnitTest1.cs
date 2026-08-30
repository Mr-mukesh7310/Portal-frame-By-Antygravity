using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StaadPortalEngine.Exporters;
using StaadPortalEngine.Generators;
using StaadPortalEngine.Helpers;
using StaadPortalEngine.Models;
using Xunit;

namespace StaadPortalEngine.Tests
{
    public class PortalGeneratorTests
    {
        [Fact]
        public void Test_BaySpacingMultiplierSyntax()
        {
            var bays = SpacingParser.ParseBaySpacings("5@6+5@4");
            Assert.Equal(10, bays.Count);
            Assert.Equal(50.0, bays.Sum());
            Assert.Equal(6.0, bays[0]);
            Assert.Equal(6.0, bays[4]);
            Assert.Equal(4.0, bays[5]);
            Assert.Equal(4.0, bays[9]);
        }

        [Fact]
        public void Test_GablePostSpacingSyntax_1at5Plus2at4()
        {
            var offsets = SpacingParser.ParseGablePostOffsets("1@5+2@4+1@5", 24.0);
            Assert.Equal(4, offsets.Count);
            Assert.Equal(5.0, offsets[0]);
            Assert.Equal(9.0, offsets[1]);
            Assert.Equal(13.0, offsets[2]);
            Assert.Equal(18.0, offsets[3]);
        }

        [Fact]
        public void Test_SingleGable_WithWidthModules_NoWallNodeOnIntermediateColumns()
        {
            var builder = new PortalGeometryBuilder();

            var config = new PortalConfiguration
            {
                ModelName = "Single Gable 24m with 2@12 Width Modules",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "5@6+5@4", // 10 bays, 11 frames
                RccWallHeight = 2.2,
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "1@5+2@4+1@5", GableBaySpacingRear = "4@6" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side_intermediate", BracketHeight = 5.0, BracketLength = 0.8 },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 4, 9 } }
            };

            var model = builder.Build(config);

            Assert.NotNull(model);
            Assert.Equal(24.0, config.TotalWidth);
            Assert.Equal(50.0, config.TotalLength);
            Assert.Equal(10, config.TotalBays);
            Assert.Equal(11, config.TotalFrames);

            // 1. Verify NO intermediate columns on Front Gable (Z = 50m) and Rear Gable (Z = 0m)
            var frontIntermediateCols = model.Beams.Values
                .Where(b => b.Type == MemberType.IntermediateColumn && Math.Abs(model.Nodes[b.NodeA].Z - 50.0) < 0.001)
                .ToList();
            Assert.Empty(frontIntermediateCols);

            var rearIntermediateCols = model.Beams.Values
                .Where(b => b.Type == MemberType.IntermediateColumn && Math.Abs(model.Nodes[b.NodeA].Z) < 0.001)
                .ToList();
            Assert.Empty(rearIntermediateCols);

            // 2. Verify Exterior Columns and Gable Posts DO have wall nodes at Y = 2.2m
            var exteriorWallNodes = model.Nodes.Values
                .Where(n => Math.Abs(n.Y - 2.2) < 0.001 && (Math.Abs(n.X) < 0.001 || Math.Abs(n.X - 24.0) < 0.001))
                .ToList();
            Assert.NotEmpty(exteriorWallNodes);

            var gableWallNodes = model.Nodes.Values
                .Where(n => Math.Abs(n.Y - 2.2) < 0.001 && (Math.Abs(n.Z) < 0.001 || Math.Abs(n.Z - 50.0) < 0.001) && n.X > 0.1 && n.X < 23.9)
                .ToList();
            Assert.NotEmpty(gableWallNodes);

            // 3. Verify Cable Tray Projection beams are NOT connected by any runner member
            var cableTrayRunners = model.Beams.Values
                .Where(b => b.Type == MemberType.CableTrayRunner)
                .ToList();
            Assert.Empty(cableTrayRunners);

            // Save sample file to dedicated Sample_Models folder for user inspection
            string outputDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Sample_Models"));
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, "Sample_3D_Industrial_Portal_Model.std");
            StaadStdWriter.WriteToFile(outputPath, model);

            // Verify STAAD Export
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("STAAD SPACE", std);
            Assert.Contains("JOINT COORDINATES", std);
            Assert.Contains("MEMBER INCIDENCES", std);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FINISH", std);
        }

        [Fact]
        public void Test_RaftersAndColumns_BrokenAtEachNodeLocation()
        {
            var builder = new PortalGeometryBuilder();

            // 24m warehouse with Wall = 2.2m, Cable Tray = 5.0m, Eave = 7.0m, Gable posts at X = 6, 12, 18
            var config = new PortalConfiguration
            {
                ModelName = "Broken Members Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                BaySpacingExpression = "2@6",
                RccWallHeight = 2.2,
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "all", BracketHeight = 5.0, BracketLength = 0.8 },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 1 } }
            };

            var model = builder.Build(config);

            // 1. Verify Left Exterior Column on Front Gable has 3 broken segments: (0->2.2), (2.2->5.0), (5.0->7.0)
            var frontLeftColBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.Column &&
                            Math.Abs(model.Nodes[b.NodeA].X) < 0.001 && Math.Abs(model.Nodes[b.NodeB].X) < 0.001 &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(3, frontLeftColBeams.Count);

            // 2. Verify Gable Post at X=6 on Front Gable has 3 broken segments: (0->2.2), (2.2->5.0), (5.0->7.6)
            var frontPostBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.GablePost &&
                            Math.Abs(model.Nodes[b.NodeA].X - 6.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].X - 6.0) < 0.001 &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(3, frontPostBeams.Count);

            // 3. Verify Rafter on Front Gable is broken at each node: (0->6), (6->12), (12->18), (18->24)
            var frontRafterBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.Rafter &&
                            Math.Abs(model.Nodes[b.NodeA].Z - 12.0) < 0.001 && Math.Abs(model.Nodes[b.NodeB].Z - 12.0) < 0.001)
                .ToList();
            Assert.Equal(4, frontRafterBeams.Count);
        }

        [Fact]
        public void Test_RoofStruts_AlongGablePosts_And_SubdividedRoofBracing()
        {
            var builder = new PortalGeometryBuilder();

            // 24m building with 4@6 gable posts (posts at X = 6, 12(ridge), 18)
            var config = new PortalConfiguration
            {
                ModelName = "Gable Post Struts Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                BaySpacingExpression = "3@6", // 3 bays, 4 frames
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" },
                Bracing = new BracingConfiguration { IncludeRoofBracing = true, IncludeWallBracing = true, BracedBayIndices = new List<int> { 0, 2 } }
            };

            var model = builder.Build(config);

            // 1. Verify longitudinal struts exist along X = 0, 6, 12, 18, 24 across all 3 bays
            var strutBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.EaveStrut || b.Type == MemberType.RidgeStrut)
                .ToList();

            // 5 strut lines * 3 bays = 15 strut members
            Assert.Equal(15, strutBeams.Count);

            // 2. Verify roof cross bracing is subdivided into 4 panels per braced bay
            // 4 panels * 2 diagonals = 8 roof braces per braced bay
            // 2 braced bays (0 and 2) => 16 roof braces total
            var roofBraceBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.RoofBracing)
                .ToList();

            Assert.Equal(16, roofBraceBeams.Count);
        }

        [Fact]
        public void Test_RoofBracing_Max12_5m_Constraint_AutoSubdivision()
        {
            var builder = new PortalGeometryBuilder();

            // Wide 30m span with 8m bay spacing and NO gable posts (half span = 15m).
            // Without intermediate struts: diag = sqrt(15^2 + 1.5^2 + 8^2) = 17.06m > 12.5m!
            // With 12.5m rule: It must insert intermediate strut lines at X = 7.5m and X = 22.5m.
            var config = new PortalConfiguration
            {
                ModelName = "Wide 30m Warehouse",
                Spans = new List<SpanDefinition> { new SpanDefinition(30.0, 8.0, 10.0) },
                BaySpacingExpression = "2@8.0",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "", GableBaySpacingRear = "" },
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0, 1 }
                }
            };

            var model = builder.Build(config);

            // Verify EVERY roof cross brace diagonal length is <= 12.5m
            var roofBraceBeams = model.Beams.Values
                .Where(b => b.Type == MemberType.RoofBracing)
                .ToList();

            Assert.NotEmpty(roofBraceBeams);
            foreach (var brace in roofBraceBeams)
            {
                var nA = model.Nodes[brace.NodeA];
                var nB = model.Nodes[brace.NodeB];
                double dx = nB.X - nA.X;
                double dy = nB.Y - nA.Y;
                double dz = nB.Z - nA.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                Assert.True(len <= 12.501, $"Roof brace member {brace.Id} length {len:F2}m exceeds 12.5m maximum limit!");
            }
        }

        [Fact]
        public void Test_CableTray_LocationOptions_NoConnectingBeams()
        {
            var builder = new PortalGeometryBuilder();

            // Case 1: Side wall only
            var configSide = new PortalConfiguration
            {
                ModelName = "Side Only",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelSide = builder.Build(configSide);
            var bracketsSide = modelSide.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.Equal(8, bracketsSide.Count);
            Assert.DoesNotContain(modelSide.Beams.Values, b => b.Type == MemberType.CableTrayRunner);

            // Case 2: Side + Intermediate
            var configInt = new PortalConfiguration
            {
                ModelName = "Side + Intermediate",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "side_intermediate", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelInt = builder.Build(configInt);
            var bracketsInt = modelInt.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.Equal(10, bracketsInt.Count);
            Assert.DoesNotContain(modelInt.Beams.Values, b => b.Type == MemberType.CableTrayRunner);

            // Case 3: Side + Intermediate + Gable
            var configAll = new PortalConfiguration
            {
                ModelName = "Side + Intermediate + Gable",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "2@8", GableBaySpacingRear = "2@8" },
                CableTray = new CableTrayConfiguration { Enabled = true, Location = "all", BracketHeight = 5.0, BracketLength = 0.8 }
            };
            var modelAll = builder.Build(configAll);
            var bracketsAll = modelAll.Beams.Values.Where(b => b.Type == MemberType.CableTrayBracket).ToList();
            Assert.True(bracketsAll.Count >= 12);
            Assert.DoesNotContain(modelAll.Beams.Values, b => b.Type == MemberType.CableTrayRunner);
        }

        [Fact]
        public void Test_SupportTypes_FixedSideWall_PinnedIntermediateAndGable()
        {
            var builder = new PortalGeometryBuilder();

            // 24m warehouse: 3 bays (4 frames), 2@12 width modules (2 interior frames * 1 int col = 2 int cols), 4@6 gable posts (2 frames * 3 posts = 6 gable posts)
            // Side wall columns: 4 frames * 2 side columns = 8 fixed supports
            // Intermediate columns: 2 interior frames * 1 post = 2 pinned supports
            // Gable posts: 2 endwalls * 3 posts = 6 pinned supports
            var config = new PortalConfiguration
            {
                ModelName = "Supports Verification Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "4@6", GableBaySpacingRear = "4@6" }
            };

            var model = builder.Build(config);

            // 1. Verify Fixed Base Nodes (Side Wall Columns)
            Assert.Equal(8, model.FixedBaseNodeIds.Count);
            foreach (var nid in model.FixedBaseNodeIds)
            {
                var n = model.Nodes[nid];
                Assert.True(Math.Abs(n.X) < 0.001 || Math.Abs(n.X - 24.0) < 0.001, $"Node {nid} at X={n.X} should be on side wall for FIXED support");
            }

            // 2. Verify Pinned Base Nodes (Intermediate Columns + Gable Posts)
            Assert.Equal(8, model.PinnedBaseNodeIds.Count);
            foreach (var nid in model.PinnedBaseNodeIds)
            {
                var n = model.Nodes[nid];
                Assert.True(n.X > 0.01 && n.X < 23.99, $"Node {nid} at X={n.X} should be interior intermediate or gable post for PINNED support");
            }

            // 3. Verify STAAD .std output contains both FIXED and PINNED support commands
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("SUPPORTS", std);
            Assert.Contains("FIXED", std);
            Assert.Contains("PINNED", std);
        }

        [Fact]
        public void Test_WallBracing_Max12_5m_Constraint_AutoSubdivision()
        {
            var builder = new PortalGeometryBuilder();

            // High eave (12m) with 8m bay spacing (Bay 0 braced, Bay 1 unbraced).
            // Without subdivision: diag = sqrt(12^2 + 8^2) = 14.42m > 12.5m!
            // With 12.5m rule:
            // 1. Subdivided into 2 tiers of 6m each (diag = sqrt(6^2 + 8^2) = 10.0m <= 12.5m).
            // 2. Wall struts at Y = 6.0m exist ONLY in Bay 0 (Z from 8m to 16m), NOT in Bay 1 (Z from 0m to 8m).
            var config = new PortalConfiguration
            {
                ModelName = "High Eave Wall Bracing Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(20.0, 12.0, 14.0) },
                BaySpacingExpression = "2@8", // Bay 0 (Z: 8->16), Bay 1 (Z: 0->8)
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    BracedBayIndices = new List<int> { 0 }
                }
            };

            var model = builder.Build(config);

            // 1. Verify all wall bracing lengths are <= 12.5m
            var wallBraces = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).ToList();
            Assert.NotEmpty(wallBraces);
            // 2 side walls * 2 tiers * 2 diagonals = 8 wall braces
            Assert.Equal(8, wallBraces.Count);

            foreach (var brace in wallBraces)
            {
                var nA = model.Nodes[brace.NodeA];
                var nB = model.Nodes[brace.NodeB];
                double dy = nB.Y - nA.Y;
                double dz = nB.Z - nA.Z;
                double len = Math.Sqrt(dy * dy + dz * dz);
                Assert.True(len <= 12.501, $"Wall brace {brace.Id} length {len:F2}m exceeds 12.5m limit!");
            }

            // 2. Verify intermediate wall struts exist in Bay 0 at Y = 6.0m
            var wallStrutsBay0 = model.Beams.Values.Where(b =>
                b.GroupName == "WALL_STRUT" &&
                Math.Abs(model.Nodes[b.NodeA].Y - 6.0) < 0.01 &&
                Math.Abs(model.Nodes[b.NodeB].Y - 6.0) < 0.01).ToList();

            // 2 wall struts (Left wall at X=0, Right wall at X=20)
            Assert.Equal(2, wallStrutsBay0.Count);

            // 3. Verify NO wall struts in unbraced Bay 1 (Z from 0 to 8m)
            var wallStrutsBay1 = model.Beams.Values.Where(b =>
                b.GroupName == "WALL_STRUT" &&
                (Math.Abs(model.Nodes[b.NodeA].Z) < 0.01 || Math.Abs(model.Nodes[b.NodeB].Z) < 0.01)).ToList();
            Assert.Empty(wallStrutsBay1);
        }

        [Fact]
        public void Test_PortalBracing_HeaderBeam_KneeBraces_And_MemberBreaking()
        {
            var builder = new PortalGeometryBuilder();

            // 4 bays (5 frames): Bay 0, 1, 2, 3 (6m each). Total length = 24m.
            // Portal Bracing in Bay 2 at Height = 4.5m with 0.5m inner leg offset
            var config = new PortalConfiguration
            {
                ModelName = "Portal Bracing Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 9.4) },
                BaySpacingExpression = "4@6", // 24m length
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    BracedBayIndices = new List<int> { 0, 2 }, // Bay 0 standard wall brace, Bay 2 portal brace
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.5,
                    PortalLegOffset = 0.5,
                    PortalBracedBayIndices = new List<int> { 2 }
                }
            };

            var model = builder.Build(config);

            // 1. Verify Portal Header Beams (Left wall + Right wall = 2 lines, each subdivided with 0.5m offset inner nodes = 6 segments)
            var portalBeams = model.Beams.Values.Where(b => b.Type == MemberType.PortalBeam).ToList();
            Assert.Equal(6, portalBeams.Count);
            foreach (var pb in portalBeams)
            {
                var nA = model.Nodes[pb.NodeA];
                var nB = model.Nodes[pb.NodeB];
                Assert.True(Math.Abs(nA.Y - 4.5) < 0.01 && Math.Abs(nB.Y - 4.5) < 0.01, "Portal beam must be at Y = 4.5m");
            }

            // 2. Verify Portal Legs / Cross Lugs (2 on Left wall + 2 on Right wall = 4 inclined legs connecting 0.5m offset node to base)
            var portalLegs = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace && b.GroupName == "PORTAL_LEG").ToList();
            Assert.Equal(4, portalLegs.Count);
            foreach (var leg in portalLegs)
            {
                var nA = model.Nodes[leg.NodeA];
                var nB = model.Nodes[leg.NodeB];
                double minY = Math.Min(nA.Y, nB.Y);
                double maxY = Math.Max(nA.Y, nB.Y);
                Assert.True(Math.Abs(minY - 0.0) < 0.01, "Portal leg must connect down to column base at Y = 0");
                Assert.True(Math.Abs(maxY - 4.5) < 0.01, "Portal leg must connect up to portal beam at Y = 4.5m");
            }

            // 3. Verify Upper Wall Cross-Bracing above Portal Beam (starts at Y = 4.5m up to Eave Y = 7.0m)
            var upperBracesInBay2 = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing &&
                (Math.Abs(model.Nodes[b.NodeA].Y - 4.5) < 0.01 || Math.Abs(model.Nodes[b.NodeB].Y - 4.5) < 0.01)).ToList();
            // 2 side walls * 2 diagonals = 4 upper braces in Bay 2
            Assert.Equal(4, upperBracesInBay2.Count);
            foreach (var ub in upperBracesInBay2)
            {
                var nA = model.Nodes[ub.NodeA];
                var nB = model.Nodes[ub.NodeB];
                double minY = Math.Min(nA.Y, nB.Y);
                double maxY = Math.Max(nA.Y, nB.Y);
                Assert.True(Math.Abs(minY - 4.5) < 0.01, "Upper brace must start at portal beam level Y = 4.5m");
                Assert.True(Math.Abs(maxY - 7.0) < 0.01, "Upper brace must end at eave level Y = 7.0m");
            }

            // 4. Verify side columns at Frame 2 and Frame 3 are broken at Y = 4.5m (portal height)
            var colBeams = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            var colNodesAt4_5 = colBeams.Select(b => model.Nodes[b.NodeA]).Concat(colBeams.Select(b => model.Nodes[b.NodeB]))
                .Where(n => Math.Abs(n.Y - 4.5) < 0.01).ToList();
            Assert.NotEmpty(colNodesAt4_5);

            // 5. Verify STAAD .std output contains ISMB350 and ISMC150 for portal members
            string std = StaadStdWriter.GenerateStdText(model);
            Assert.Contains("ISMB350", std);
            Assert.Contains("ISMC150", std);
        }

        [Fact]
        public void Test_PortalBracing_UpperCrossBracing_Subdivision_WhenDiagonalExceedsMax()
        {
            var builder = new PortalGeometryBuilder();

            // High eave height: 16m eave height, 4m portal height -> Upper Dy = 12m, BayZ = 8m -> Diagonal = 14.42m > 12.5m
            var config = new PortalConfiguration
            {
                ModelName = "Portal Subdivided Upper Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 16.0, 18.4) },
                BaySpacingExpression = "3@8", // 24m length
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = false,
                    IncludeWallBracing = true,
                    MaxBraceLength = 12.5,
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.0,
                    PortalLegOffset = 0.5,
                    BracedBayIndices = new List<int> { 1 },
                    PortalBracedBayIndices = new List<int> { 1 } // Bay 1
                }
            };

            var model = builder.Build(config);

            // 1. Upper Dy = 12m, Bay = 8m -> Subdivides into 2 tiers with subDy = 6m -> Wall strut at Y = 4 + 6 = 10m
            var upperWallStruts = model.Beams.Values.Where(b => b.Type == MemberType.EaveStrut && b.GroupName == "WALL_STRUT").ToList();
            Assert.NotEmpty(upperWallStruts);
            foreach (var ws in upperWallStruts)
            {
                var nA = model.Nodes[ws.NodeA];
                Assert.True(Math.Abs(nA.Y - 10.0) < 0.01, "Upper wall strut must be placed at intermediate tier Y = 10.0m");
            }

            // 2. Verify all upper cross braces in Bay 1 have length <= 12.5m
            var upperBraces = model.Beams.Values.Where(b => b.Type == MemberType.WallBracing).ToList();
            // 2 side walls * 2 tiers * 2 diagonals = 8 upper braces in Bay 1
            Assert.Equal(8, upperBraces.Count);
            foreach (var ub in upperBraces)
            {
                var nA = model.Nodes[ub.NodeA];
                var nB = model.Nodes[ub.NodeB];
                double length = Math.Sqrt(Math.Pow(nA.X - nB.X, 2) + Math.Pow(nA.Y - nB.Y, 2) + Math.Pow(nA.Z - nB.Z, 2));
                Assert.True(length <= 12.51, $"Upper brace length {length} must be <= 12.5m");
            }

            // 3. Verify side columns at Frame 1 and Frame 2 have nodes at Y = 4.0m, Y = 10.0m, and Y = 16.0m
            var colBeams = model.Beams.Values.Where(b => b.Type == MemberType.Column).ToList();
            var colNodes = colBeams.Select(b => model.Nodes[b.NodeA]).Concat(colBeams.Select(b => model.Nodes[b.NodeB])).ToList();
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 4.0) < 0.01);
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 10.0) < 0.01);
            Assert.Contains(colNodes, n => Math.Abs(n.Y - 16.0) < 0.01);
        }

        [Fact]
        public void Test_StaadStdWriter_GroupDefinition_And_MemberSpecification_Format()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "STAAD Syntax Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(20.0, 7.0, 10.0) },
                BaySpacingExpression = "3@6",
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    BracedBayIndices = new List<int> { 0, 2 }
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Check Group Definition format
            Assert.Contains("START GROUP DEFINITION", std);
            Assert.Contains("MEMBER\r\n_FRAME", std);
            Assert.Contains("_COLUMNS", std);
            Assert.Contains("_RAFTERS", std);
            Assert.Contains("_BRACING", std);
            Assert.Contains("END GROUP DEFINITION", std);

            // 2. Check Member Specification format (TENSION for cross bracing, TRUSS for strut pipes)
            Assert.Contains("******MEMBER SPECIFICATION", std);
            Assert.Contains("MEMBER TENSION", std);
            Assert.Contains("MEMBER TRUSS", std);
            Assert.Contains("G 7.88462e+07", std);
            Assert.Contains("STRENGTH RY 1.5 RT 1.2", std);
            Assert.NotEmpty(model.TensionOnlyBeamIds);
            Assert.NotEmpty(model.PinnedEndBeamIds);

            // 3. Check Range compression
            var sampleIds = new List<int> { 1, 2, 3, 4, 5, 8, 10, 11, 12 };
            var ranges = StaadStdWriter.CompressToStaadRanges(sampleIds);
            Assert.Contains("1 TO 5", ranges);
            Assert.Contains("8", ranges);
            Assert.Contains("10 TO 12", ranges);
        }

        [Fact]
        public void Test_PortalReleases_GablePostBeta90_And_GablePostRafterReleases()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Portal & Gable Release Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 8.0, 10.0) },
                BaySpacingExpression = "3@6",
                GableWalls = new GableWallConfiguration { GableBaySpacingFront = "3@8", GableBaySpacingRear = "3@8" },
                Bracing = new BracingConfiguration
                {
                    IncludeRoofBracing = true,
                    IncludeWallBracing = true,
                    EnablePortalBracing = true,
                    PortalBracingHeight = 4.0,
                    PortalLegOffset = 0.5,
                    BracedBayIndices = new List<int> { 1 },
                    PortalBracedBayIndices = new List<int> { 1 }
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify BETA 90 MEMB assigned to Gable Posts under CONSTANTS
            Assert.NotEmpty(model.Beta90BeamIds);
            var gableBeamIds = model.Beams.Values.Where(b => b.Type == MemberType.GablePost).Select(b => b.Id).ToList();
            foreach (var gId in gableBeamIds)
            {
                Assert.Contains(gId, model.Beta90BeamIds);
            }
            Assert.Contains("BETA 90 MEMB", std);

            // 2. Verify MEMBER RELEASE block format
            Assert.Contains("MEMBER RELEASE", std);

            // 3. Verify Gable Post top releases connecting to rafter
            Assert.NotEmpty(model.EndReleaseMyMzBeamIds);
            Assert.Contains("END MY MZ", std);

            // 4. Verify Portal Beam releases at column connections
            Assert.NotEmpty(model.StartReleaseMyMzBeamIds);
            Assert.Contains("START MY MZ", std);

            // 5. Verify Portal Leg releases at base
            var portalLegs = model.Beams.Values.Where(b => b.Type == MemberType.PortalKneeBrace && b.GroupName == "PORTAL_LEG").ToList();
            Assert.NotEmpty(portalLegs);
            foreach (var leg in portalLegs)
            {
                Assert.Contains(leg.Id, model.EndReleaseMyMzBeamIds);
            }
        }

        [Fact]
        public void Test_JackPortal_IntermediateColumns_JackBeams_And_TieMembers()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12", // Intermediate column line at X = 12m
                BaySpacingExpression = "6@6",  // Total 36m length with frames at Z = 0, 6, 12, 18, 24, 30, 36
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "3@12" // Columns along X = 12m only at Z = 0, 12, 24, 36
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // 1. Verify intermediate columns exist: full columns at Z = 12 and 24, and stub posts at skipped frames Z = 6, 18, 30
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);

            // Full columns have base at Y = 0
            var fullColBaseNodes = intCols.Select(b => model.Nodes[b.NodeA]).Concat(intCols.Select(b => model.Nodes[b.NodeB]))
                .Where(n => Math.Abs(n.X - 12.0) < 0.05 && Math.Abs(n.Y - 0.0) < 0.05).Select(n => n.Z).Distinct().ToList();
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 12.0) < 0.05);
            Assert.Contains(fullColBaseNodes, z => Math.Abs(z - 24.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 6.0) < 0.05);
            Assert.DoesNotContain(fullColBaseNodes, z => Math.Abs(z - 18.0) < 0.05);

            // 2. Verify Jack Beams exist 1m below rafter level (Y = 8.2 - 1.0 = 7.2)
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.NotEmpty(jackBeams);
            Assert.Contains("_JACKBEAMS", std);
            var jackNodes = jackBeams.Select(b => model.Nodes[b.NodeA]).Concat(jackBeams.Select(b => model.Nodes[b.NodeB])).ToList();
            double expectedJackY = 7.0 + 1.2 - 1.0; // Y_rafter(12m) - 1.0 = 8.2 - 1.0 = 7.2
            Assert.All(jackNodes, n => Assert.True(Math.Abs(n.Y - expectedJackY) < 0.1));

            // 3. Verify stub posts exist at skipped frames (Z = 6, 18, 30) from Y = 7.2 to Y = 8.2
            var stubPosts = model.Beams.Values.Where(b => b.GroupName == "JACK_POST").ToList();
            Assert.NotEmpty(stubPosts);
            var stubPostZ = stubPosts.Select(b => model.Nodes[b.NodeA].Z).Distinct().ToList();
            Assert.Contains(stubPostZ, z => Math.Abs(z - 6.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 18.0) < 0.05);
            Assert.Contains(stubPostZ, z => Math.Abs(z - 30.0) < 0.05);

            // 4. Verify releases: stub posts have BOTH Start and End releases (top and bottom)
            foreach (var sp in stubPosts)
            {
                Assert.Contains(sp.Id, model.StartReleaseMyMzBeamIds);
                Assert.Contains(sp.Id, model.EndReleaseMyMzBeamIds);
            }

            // 5. Verify full columns have top release connecting to rafter
            Assert.NotEmpty(model.EndReleaseMyMzBeamIds);

            // 6. Verify ASSIGN COLUMN and _JACKPOSTS in STAAD output
            Assert.Contains("ASSIGN COLUMN", std);
            Assert.Contains("_JACKPOSTS", std);

            // 7. Verify Jack Beams are NOT released
            foreach (var jb in jackBeams)
            {
                Assert.DoesNotContain(jb.Id, model.StartReleaseMyMzBeamIds);
                Assert.DoesNotContain(jb.Id, model.EndReleaseMyMzBeamIds);
            }

            // 8. Verify Jack Portal intermediate columns and stub posts have BETA 90
            foreach (var col in intCols)
            {
                Assert.Contains(col.Id, model.Beta90BeamIds);
            }
            Assert.Contains("BETA 90 MEMB", std);
        }

        [Fact]
        public void Test_JackPortal_NoJackBeam_When_Columns_At_Each_Frame()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal No Skipped Frames Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12",
                BaySpacingExpression = "6@6",
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "6@6" // Columns at every frame (no skipped frames)
                }
            };

            var model = builder.Build(config);

            // 1. Verify no Jack Beams are generated since columns exist at every frame
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.Empty(jackBeams);

            // 2. Verify no dummy 1m-below-rafter nodes exist on columns
            double expectedJackY = 7.0 + 1.2 - 1.0; // 7.2m
            var intColNodes = model.Nodes.Values.Where(n => Math.Abs(n.X - 12.0) < 0.05).ToList();
            Assert.DoesNotContain(intColNodes, n => Math.Abs(n.Y - expectedJackY) < 0.05);

            // 3. Verify no top releases on intermediate columns
            var intColBeams = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).Select(b => b.Id).ToList();
            foreach (var bid in intColBeams)
            {
                Assert.DoesNotContain(bid, model.EndReleaseMyMzBeamIds);
                Assert.DoesNotContain(bid, model.StartReleaseMyMzBeamIds);
                Assert.DoesNotContain(bid, model.Beta90BeamIds);
            }
        }

        [Fact]
        public void Test_JackPortal_FrontToRear_Layout()
        {
            var builder = new PortalGeometryBuilder();
            var config = new PortalConfiguration
            {
                ModelName = "Jack Portal Front To Rear Test",
                Spans = new List<SpanDefinition> { new SpanDefinition(24.0, 7.0, 10.0) },
                WidthModuleExpression = "2@12", // Intermediate column line at X = 12m
                BaySpacingExpression = "5@6+5@4",  // Total 50m length (Front Z=50 down to Rear Z=0)
                JackPortal = new JackPortalConfiguration
                {
                    Enabled = true,
                    IntermediateBaySpacingExpression = "2@8+1@10+4@6" // Stepping from Front: Z = 50, 42, 34, 24, 18, 12, 6, 0
                }
            };

            var model = builder.Build(config);
            string std = StaadStdWriter.GenerateStdText(model);

            // Verify intermediate columns exist along X = 12m at Front-to-Rear coordinates
            var intCols = model.Beams.Values.Where(b => b.Type == MemberType.IntermediateColumn).ToList();
            Assert.NotEmpty(intCols);

            // Jack Beams exist 1m below rafter level
            var jackBeams = model.Beams.Values.Where(b => b.Type == MemberType.JackBeam).ToList();
            Assert.NotEmpty(jackBeams);
            Assert.Contains("_JACKBEAMS", std);

            // Stub posts have ASSIGN COLUMN
            Assert.Contains("ASSIGN COLUMN", std);
        }
    }
}

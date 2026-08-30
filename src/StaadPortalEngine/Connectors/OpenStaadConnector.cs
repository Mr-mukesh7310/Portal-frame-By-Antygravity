using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StaadPortalEngine.Generators;
using StaadPortalEngine.Models;

namespace StaadPortalEngine.Connectors
{
    public class OpenStaadResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int NodesCreated { get; set; }
        public int BeamsCreated { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public static class OpenStaadConnector
    {
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

        private static object? GetActiveComObject(string progId)
        {
            try
            {
                int hr = CLSIDFromProgID(progId, out Guid clsid);
                if (hr >= 0)
                {
                    GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
                    return obj;
                }
            }
            catch
            {
                // Ignored, will fallback
            }
            return null;
        }

        public static OpenStaadResult PushToActiveStaad(GeneratedModel model)
        {
            var result = new OpenStaadResult();

            try
            {
                // Attempt 1: Get active running instance from Windows Running Object Table (ROT)
                object? staadApp = GetActiveComObject("StaadPro.OpenSTAAD");

                // Attempt 2: Fallback to Type activation
                if (staadApp == null)
                {
                    Type? staadType = Type.GetTypeFromProgID("StaadPro.OpenSTAAD");
                    if (staadType != null)
                    {
                        staadApp = Activator.CreateInstance(staadType);
                    }
                }

                if (staadApp == null)
                {
                    result.Success = false;
                    result.Message = "Could not find or connect to a running STAAD.Pro session. Please ensure STAAD.Pro is open with a new structure file.";
                    return result;
                }

                dynamic staad = staadApp;
                dynamic geometry = staad.Geometry;

                if (geometry == null)
                {
                    result.Success = false;
                    result.Message = "Failed to access STAAD.Pro Geometry interface. Ensure a structural model is open in STAAD.Pro.";
                    return result;
                }

                // 1. Create Nodes
                int nCreated = 0;
                foreach (var node in model.Nodes.Values)
                {
                    geometry.CreateNode(node.Id, node.X, node.Y, node.Z);
                    nCreated++;
                }

                // 2. Create Beams
                int bCreated = 0;
                foreach (var beam in model.Beams.Values)
                {
                    geometry.CreateBeam(beam.Id, beam.NodeA, beam.NodeB);
                    bCreated++;
                }

                result.Success = true;
                result.NodesCreated = nCreated;
                result.BeamsCreated = bCreated;
                result.Message = $"Successfully created {nCreated} nodes and {bCreated} members in STAAD.Pro!";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"OpenSTAAD Bridge Error: {ex.Message}";
            }

            return result;
        }
    }
}

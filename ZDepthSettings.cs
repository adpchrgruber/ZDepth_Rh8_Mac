using System.Globalization;
using Rhino;

namespace ZDepth
{
    /// <summary>
    /// How depth becomes grey. The near/far range belongs to the scene, so it is saved in
    /// the document; which end is white is a preference, saved with Rhino's settings.
    /// </summary>
    internal static class ZDepthSettings
    {
        const string Section = "ZDepth";

        /// <summary>True (default): near is white, far and background black.</summary>
        public static bool NearIsWhite
        {
            get => ZDepthPlugin.Instance?.Settings.GetBool("NearIsWhite", true) ?? true;
            set => ZDepthPlugin.Instance?.Settings.SetBool("NearIsWhite", value);
        }

        /// <summary>
        /// The document's fixed range in model units, or false when the range is automatic
        /// (fitted to the visible geometry every frame).
        /// </summary>
        public static bool TryGetFixedRange(RhinoDoc doc, out double near, out double far)
        {
            near = far = 0;
            if (doc == null)
                return false;
            return double.TryParse(doc.Strings.GetValue(Section, "Near"), NumberStyles.Float, CultureInfo.InvariantCulture, out near)
                && double.TryParse(doc.Strings.GetValue(Section, "Far"), NumberStyles.Float, CultureInfo.InvariantCulture, out far)
                && far > near;
        }

        public static void SetFixedRange(RhinoDoc doc, double near, double far)
        {
            doc.Strings.SetString(Section, "Near", near.ToString("R", CultureInfo.InvariantCulture));
            doc.Strings.SetString(Section, "Far", far.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void SetAutoRange(RhinoDoc doc)
        {
            doc.Strings.Delete(Section, "Near");
            doc.Strings.Delete(Section, "Far");
        }

        public static string Describe(RhinoDoc doc)
        {
            var range = TryGetFixedRange(doc, out var near, out var far)
                ? string.Format(CultureInfo.InvariantCulture, "fixed {0:G6} to {1:G6}", near, far)
                : "auto";
            return "range " + range + ", near is " + (NearIsWhite ? "white" : "black");
        }
    }
}

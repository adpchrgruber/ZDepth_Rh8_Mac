using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ZDepth
{
    /// <summary>
    /// Sets how depth maps to grey: automatic or fixed near/far, and which end is white.
    /// Applies to the Z Depth view and to ZDepthCapture. Changes show live.
    /// </summary>
    public class ZDepthRangeCommand : Command
    {
        public override string EnglishName => "ZDepthRange";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            bool wasFixed = ZDepthSettings.TryGetFixedRange(doc, out var oldNear, out var oldFar);
            bool oldNearIsWhite = ZDepthSettings.NearIsWhite;

            bool isFixed = wasFixed;
            double near = wasFixed ? oldNear : 0, far = wasFixed ? oldFar : 0;
            if (!wasFixed && !SuggestRange(doc, out near, out far))
            {
                near = 0;
                far = 1000;
            }

            while (true)
            {
                var go = new GetOption();
                go.SetCommandPrompt("Z Depth " + ZDepthSettings.Describe(doc) + ". Press Enter when done");
                go.AcceptNothing(true);
                var rangeToggle = new OptionToggle(isFixed, "Auto", "Fixed");
                var nearIsWhite = new OptionToggle(ZDepthSettings.NearIsWhite, "Black", "White");
                var nearOption = new OptionDouble(near);
                var farOption = new OptionDouble(far);
                int rangeIndex = go.AddOptionToggle("Range", ref rangeToggle);
                int nearIndex = -1, farIndex = -1;
                if (isFixed)
                {
                    nearIndex = go.AddOptionDouble("Near", ref nearOption);
                    farIndex = go.AddOptionDouble("Far", ref farOption);
                }
                int fromViewIndex = go.AddOption("FromView");
                int whiteIndex = go.AddOptionToggle("NearIs", ref nearIsWhite);

                var res = go.Get();
                if (res == GetResult.Nothing)
                    return Result.Success;
                if (res != GetResult.Option)
                {
                    // Cancelled: put everything back.
                    if (wasFixed)
                        ZDepthSettings.SetFixedRange(doc, oldNear, oldFar);
                    else
                        ZDepthSettings.SetAutoRange(doc);
                    ZDepthSettings.NearIsWhite = oldNearIsWhite;
                    doc.Views.Redraw();
                    return Result.Cancel;
                }

                int index = go.Option().Index;
                if (index == rangeIndex)
                    isFixed = rangeToggle.CurrentValue;
                else if (index == nearIndex)
                    near = nearOption.CurrentValue;
                else if (index == farIndex)
                    far = farOption.CurrentValue;
                else if (index == fromViewIndex)
                {
                    if (SuggestRange(doc, out near, out far))
                        isFixed = true;
                    else
                        RhinoApp.WriteLine("No surfaces visible in the active view.");
                }
                else if (index == whiteIndex)
                    ZDepthSettings.NearIsWhite = nearIsWhite.CurrentValue;

                if (isFixed && far <= near)
                {
                    RhinoApp.WriteLine("Far must be greater than Near.");
                    far = near + 1;
                }
                if (isFixed)
                    ZDepthSettings.SetFixedRange(doc, near, far);
                else
                    ZDepthSettings.SetAutoRange(doc);
                doc.Views.Redraw();
            }
        }

        /// <summary>Nearest and farthest visible surface in the active view.</summary>
        static bool SuggestRange(RhinoDoc doc, out double near, out double far)
        {
            near = far = 0;
            var view = doc.Views.ActiveView;
            var conduit = ZDepthPlugin.Instance.Conduit;
            if (view == null || conduit == null)
                return false;
            var size = view.ActiveViewport.Size;
            var image = DepthCapture.Render(conduit, doc, view, size.Width, size.Height, out _);
            if (image == null || !image.VisibleRange.IsValid)
                return false;
            near = image.VisibleRange.T0;
            far = image.VisibleRange.T1;
            return true;
        }
    }
}

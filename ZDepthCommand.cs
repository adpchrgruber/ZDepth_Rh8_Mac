using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace ZDepth
{
    /// <summary>Toggles the active viewport between Z Depth and the mode it had before.</summary>
    public class ZDepthCommand : Command
    {
        readonly Dictionary<Guid, Guid> _previousModes = new Dictionary<Guid, Guid>();

        public ZDepthCommand()
        {
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static ZDepthCommand Instance { get; private set; }

        public override string EnglishName => "ZDepth";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null)
                return Result.Failure;
            var depth = ZDepthDisplayMode.Ensure();
            if (depth == null)
                return Result.Failure;

            var viewport = view.ActiveViewport;
            if (viewport.DisplayMode?.Id == depth.Id)
            {
                var previous = _previousModes.TryGetValue(viewport.Id, out var id) ? DisplayModeDescription.GetDisplayMode(id) : null;
                viewport.DisplayMode = previous ?? DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
                RhinoApp.WriteLine("Z Depth off in {0}.", viewport.Name);
            }
            else
            {
                if (viewport.DisplayMode != null)
                    _previousModes[viewport.Id] = viewport.DisplayMode.Id;
                viewport.DisplayMode = depth;
                RhinoApp.WriteLine("Z Depth on in {0} ({1}). Run ZDepth again to switch back; ZDepthRange to change the range.",
                    viewport.Name, ZDepthSettings.Describe(doc));
            }
            view.Redraw();
            return Result.Success;
        }
    }
}

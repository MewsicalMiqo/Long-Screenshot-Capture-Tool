# Long Screenshot Capture Tool

A WPF utility for capturing "long" (tall, scrolled) screenshots of a single window by
grabbing multiple frames as you scroll and stitching them together.

## How it works

1. **Select the target window** in the dropdown (or click **Refresh** to re-list open windows).
   Keep the target window in front of the tool so it isn't occluded when each frame is grabbed.
2. Click **Capture frame** to grab the current state of the target window.
3. **Drag a selection** over the first frame to choose the region to stitch.
4. Scroll the window and keep clicking **Capture frame** for each subsequent position.
5. **Save preview** exports the stitched composite as a PNG.

## Notes

- Frames are aligned with a row-difference heuristic, so scroll *slowly and steadily* for the
  best result. Frames that scroll by more than one screen height are dropped (and reported).
- Undo removes the most recent frame; Undo back to the first frame lets you re-select the region.
- Requires Windows (Win32 screen capture).

// The two steps around the pose model that transformers.js 3.8.1 does not do: its ViTPose processor
// resizes the whole frame instead of cutting out the person, and its post-processing ignores where the
// box was. So the person is cut out here (cropForPose) and the heatmaps are read here (readHeatmaps).
// Plain arithmetic on typed arrays, no canvas, so `node` can run the same code against the model.

/** The model's input: 192 × 256, the 3:4 upright box ViTPose was trained on. */
export const POSE_WIDTH = 192;
export const POSE_HEIGHT = 256;

/** The detector's box is grown by this much, so a hand or a foot just outside it is still seen. */
const MARGIN = 1.25;

/**
 * Cuts a person out of a frame for the pose model. The box (normalised x0, y0, x1, y1) is widened or
 * heightened about its centre to 3:4, grown by MARGIN and sampled (bilinear) to 192 × 256 RGB; whatever
 * falls outside the frame is black. Returns the pixels and the region they came from, in frame pixels.
 * @param {{ width: number, height: number, data: Uint8ClampedArray }} pixels the frame, RGBA
 */
export function cropForPose(pixels, box) {
  const { width, height, data } = pixels;
  const cx = (box.x0 + box.x1) / 2 * width, cy = (box.y0 + box.y1) / 2 * height;
  let w = (box.x1 - box.x0) * width, h = (box.y1 - box.y0) * height;
  if (w / h > POSE_WIDTH / POSE_HEIGHT) h = w * POSE_HEIGHT / POSE_WIDTH;
  else w = h * POSE_WIDTH / POSE_HEIGHT;
  w *= MARGIN;
  h *= MARGIN;
  const left = cx - w / 2, top = cy - h / 2;

  const out = new Uint8ClampedArray(POSE_WIDTH * POSE_HEIGHT * 3);
  for (let y = 0; y < POSE_HEIGHT; y++) {
    const fy = top + (y + 0.5) * h / POSE_HEIGHT - 0.5;
    const y0 = Math.floor(fy), ty = fy - y0;
    for (let x = 0; x < POSE_WIDTH; x++) {
      const fx = left + (x + 0.5) * w / POSE_WIDTH - 0.5;
      const x0 = Math.floor(fx), tx = fx - x0;
      if (x0 < -1 || y0 < -1 || x0 >= width || y0 >= height) continue;
      const o = (y * POSE_WIDTH + x) * 3;
      for (let c = 0; c < 3; c++) {
        const at = (px, py) => px < 0 || py < 0 || px >= width || py >= height ? 0 : data[(py * width + px) * 4 + c];
        out[o + c] = (at(x0, y0) * (1 - tx) + at(x0 + 1, y0) * tx) * (1 - ty) + (at(x0, y0 + 1) * (1 - tx) + at(x0 + 1, y0 + 1) * tx) * ty;
      }
    }
  }
  return { data: out, left, top, width: w, height: h };
}

/**
 * Reads one person's heatmaps (a Float32Array of joints × rows × columns) into 17 × (x, y, score): each
 * joint sits at its map's brightest cell, nudged a quarter of a cell towards the brighter neighbour, and
 * is placed back in the frame through the region the crop came from. x and y are both divided by the
 * frame's width, so angles between joints are true whatever the frame's aspect (PostureRules.cs).
 */
export function readHeatmaps(heat, joints, rows, columns, region, frameWidth) {
  const keypoints = new Array(joints * 3).fill(0);
  for (let j = 0; j < joints; j++) {
    const base = j * rows * columns;
    let best = base;
    for (let i = base + 1; i < base + rows * columns; i++) if (heat[i] > heat[best]) best = i;
    const row = Math.floor((best - base) / columns), column = (best - base) % columns;
    let x = column, y = row;
    if (column > 0 && column < columns - 1) x += 0.25 * Math.sign(heat[best + 1] - heat[best - 1]);
    if (row > 0 && row < rows - 1) y += 0.25 * Math.sign(heat[best + columns] - heat[best - columns]);
    keypoints[j * 3] = (region.left + (x + 0.5) / columns * region.width) / frameWidth;
    keypoints[j * 3 + 1] = (region.top + (y + 0.5) / rows * region.height) / frameWidth;
    keypoints[j * 3 + 2] = heat[best];
  }
  return keypoints;
}

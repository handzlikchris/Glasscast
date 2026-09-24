// View geometry. The server sends 600×600 frames with the source letterboxed
// in the middle (server/Desktop/RegionMath.cs Fit); these helpers mirror that
// so taps and drags on the glasses map to the right monitor pixels.
import type { Region, Size } from './protocol';

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface Point {
  x: number;
  y: number;
}

export const VIEW_SIZE: Size = { width: 600, height: 600 };
export const MIN_REGION_SIZE = 160;

export type AspectName = 'square' | 'wide';
export const ASPECTS: Record<AspectName, number> = { square: 1, wide: 16 / 9 };

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** Largest rect with the source's aspect ratio that fits in the frame, centred. */
export function fit(source: Size, frame: Size = VIEW_SIZE): Rect {
  const scale = Math.min(frame.width / source.width, frame.height / source.height);
  const width = Math.max(1, Math.round(source.width * scale));
  const height = Math.max(1, Math.round(source.height * scale));
  return {
    x: Math.floor((frame.width - width) / 2),
    y: Math.floor((frame.height - height) / 2),
    width,
    height,
  };
}

/** Keeps a region on the monitor and at least MIN_REGION_SIZE (mirrors RegionMath.Clamp). */
export function clampRegion(region: Region, monitor: Size): Region {
  const width = Math.round(clamp(region.width, Math.min(MIN_REGION_SIZE, monitor.width), monitor.width));
  const height = Math.round(clamp(region.height, Math.min(MIN_REGION_SIZE, monitor.height), monitor.height));
  return {
    x: Math.round(clamp(region.x, 0, monitor.width - width)),
    y: Math.round(clamp(region.y, 0, monitor.height - height)),
    width,
    height,
  };
}

/** Where a monitor region appears in the overview (whole monitor letterboxed into the view). */
export function regionInOverview(region: Region, monitor: Size, frame: Size = VIEW_SIZE): Rect {
  const view = fit(monitor, frame);
  const scale = view.width / monitor.width;
  return {
    x: view.x + region.x * scale,
    y: view.y + region.y * scale,
    width: region.width * scale,
    height: region.height * scale,
  };
}

/** Moves a region by a drag measured in overview pixels. */
export function moveRegion(region: Region, dxView: number, dyView: number, monitor: Size, frame: Size = VIEW_SIZE): Region {
  const scale = monitor.width / fit(monitor, frame).width;
  return clampRegion({ ...region, x: region.x + dxView * scale, y: region.y + dyView * scale }, monitor);
}

/** Scales a region about its centre, keeping the given aspect ratio. */
export function resizeRegion(region: Region, factor: number, aspect: number, monitor: Size): Region {
  const cx = region.x + region.width / 2;
  const cy = region.y + region.height / 2;
  let height = region.height * factor;
  let width = height * aspect;

  // Shrink to fit the monitor without breaking the aspect ratio.
  const shrink = Math.min(1, monitor.width / width, monitor.height / height);
  width *= shrink;
  height *= shrink;
  if (height < MIN_REGION_SIZE) {
    height = MIN_REGION_SIZE;
    width = height * aspect;
  }

  return clampRegion({ x: cx - width / 2, y: cy - height / 2, width, height }, monitor);
}

/** Where a region's content sits inside the 600×600 view (letterboxed). */
export function contentRect(region: Region, frame: Size = VIEW_SIZE): Rect {
  return fit({ width: region.width, height: region.height }, frame);
}

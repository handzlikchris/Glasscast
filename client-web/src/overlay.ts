// Draws the software cursor and the region box on the overlay canvas.
// Bright colours only: on the additive display dark pixels are invisible.
import type { Point, Rect } from './geometry';

export type Look = 'natural' | 'lifted' | 'contrast';

/** Edge the view is panning towards (-1/0/1 per axis), drawn as a glow on that edge. */
export interface EdgeGlow {
  rect: Rect;
  x: number;
  y: number;
}

export interface OverlayState {
  cursor: Point | null;
  regionBox: Rect | null;
  look: Look;
  edgeGlow?: EdgeGlow | null;
}

export function drawOverlay(ctx: CanvasRenderingContext2D, state: OverlayState): void {
  ctx.clearRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  if (state.edgeGlow) drawEdgeGlow(ctx, state.edgeGlow);
  if (state.regionBox) drawRegionBox(ctx, state.regionBox);
  if (state.cursor) drawCursor(ctx, state.cursor, state.look === 'contrast');
}

function drawEdgeGlow(ctx: CanvasRenderingContext2D, { rect, x, y }: EdgeGlow): void {
  const depth = 18;
  ctx.save();
  const bar = (x0: number, y0: number, x1: number, y1: number, w: number, h: number, bx: number, by: number) => {
    const g = ctx.createLinearGradient(x0, y0, x1, y1);
    g.addColorStop(0, 'rgba(92, 214, 230, 0.85)');
    g.addColorStop(1, 'rgba(92, 214, 230, 0)');
    ctx.fillStyle = g;
    ctx.fillRect(bx, by, w, h);
  };
  const { x: rx, y: ry, width: rw, height: rh } = rect;
  if (x < 0) bar(rx, 0, rx + depth, 0, depth, rh, rx, ry);
  if (x > 0) bar(rx + rw, 0, rx + rw - depth, 0, depth, rh, rx + rw - depth, ry);
  if (y < 0) bar(0, ry, 0, ry + depth, rw, depth, rx, ry);
  if (y > 0) bar(0, ry + rh, 0, ry + rh - depth, rw, depth, rx, ry + rh - depth);
  ctx.restore();
}

function drawRegionBox(ctx: CanvasRenderingContext2D, r: Rect): void {
  ctx.save();
  ctx.lineWidth = 3;
  ctx.strokeStyle = '#ffd166';
  ctx.strokeRect(r.x + 1.5, r.y + 1.5, r.width - 3, r.height - 3);

  // Corner ticks make the box readable even over busy content.
  const tick = Math.min(18, r.width / 4, r.height / 4);
  ctx.lineWidth = 6;
  ctx.beginPath();
  for (const [cx, cy, sx, sy] of [
    [r.x, r.y, 1, 1],
    [r.x + r.width, r.y, -1, 1],
    [r.x, r.y + r.height, 1, -1],
    [r.x + r.width, r.y + r.height, -1, -1],
  ]) {
    ctx.moveTo(cx, cy + sy * tick);
    ctx.lineTo(cx, cy);
    ctx.lineTo(cx + sx * tick, cy);
  }
  ctx.stroke();
  ctx.restore();
}

function drawCursor(ctx: CanvasRenderingContext2D, p: Point, highContrast: boolean): void {
  const scale = highContrast ? 1.9 : 1.2;
  ctx.save();
  ctx.translate(Math.round(p.x), Math.round(p.y));

  if (highContrast) {
    ctx.beginPath();
    ctx.arc(0, 0, 22, 0, Math.PI * 2);
    ctx.lineWidth = 3;
    ctx.strokeStyle = '#ffd166';
    ctx.stroke();
  }

  ctx.scale(scale, scale);
  ctx.beginPath();
  ctx.moveTo(0, 0);
  ctx.lineTo(0, 17);
  ctx.lineTo(4.5, 12.5);
  ctx.lineTo(8, 20);
  ctx.lineTo(11, 18.5);
  ctx.lineTo(7.5, 11.5);
  ctx.lineTo(13, 11.5);
  ctx.closePath();
  ctx.fillStyle = highContrast ? '#ffd166' : '#ffffff';
  ctx.fill();
  ctx.lineWidth = 1.5;
  ctx.strokeStyle = '#000000';
  ctx.stroke();
  ctx.restore();
}

// A diagnostic screen: what the glasses' page receives from its sensors and from a pinch-drag, to
// find out whether the Neural Band's wrist rotation reaches web apps and through what. Meta's
// docs (2026-11-12) mention "wrist motion from Meta Neural Band" for web apps but document only
// DeviceMotionEvent / DeviceOrientationEvent, without saying whether they're the head or the wrist.
//
// It shows the live values, and after each pinch how far each one moved during it (the numbers
// to read out). Swipe left leaves. Nothing is sent anywhere.
import { useEffect, useRef, useState } from 'react';

type Values = Record<string, number | string | boolean | null>;

const round = (v: unknown): number | string | boolean | null =>
  typeof v === 'number' ? Math.round(v * 10) / 10 : typeof v === 'string' || typeof v === 'boolean' ? v : v == null ? null : String(v);

/** Window and navigator names that might be a wrist or band API. */
function candidates(): string[] {
  const names = new Set<string>();
  const look = /wrist|neural|band|emg|gesture|xr|hand|imu|sensor|gyro|orient|motion/i;
  for (const o of [window, navigator] as object[]) {
    for (let p: object | null = o; p; p = Object.getPrototypeOf(p)) {
      for (const k of Object.getOwnPropertyNames(p)) if (look.test(k)) names.add(`${o === window ? 'window' : 'navigator'}.${k}`);
    }
  }
  return [...names].sort();
}

export function SensorProbe({ onLeave }: { onLeave(): void }) {
  const live = useRef<Values>({});
  const counts = useRef<Record<string, number>>({});
  /** Each numeric value's range during the current pinch. */
  const during = useRef<Record<string, { min: number; max: number }> | null>(null);
  const [, redraw] = useState(0);
  const [lastPinch, setLastPinch] = useState<[string, number][]>([]);
  const [started, setStarted] = useState(false);
  const found = useRef(candidates());

  const put = (prefix: string, values: Values) => {
    counts.current[prefix] = (counts.current[prefix] ?? 0) + 1;
    for (const [k, v] of Object.entries(values)) {
      const key = `${prefix}.${k}`;
      live.current[key] = v;
      const range = during.current;
      if (range && typeof v === 'number') {
        const r = range[key];
        range[key] = r ? { min: Math.min(r.min, v), max: Math.max(r.max, v) } : { min: v, max: v };
      }
    }
  };

  useEffect(() => {
    const orient = (e: DeviceOrientationEvent) =>
      put(e.type === 'deviceorientationabsolute' ? 'orientAbs' : 'orient', {
        alpha: round(e.alpha), beta: round(e.beta), gamma: round(e.gamma), absolute: e.absolute,
      });
    const motion = (e: DeviceMotionEvent) =>
      put('motion', {
        rotA: round(e.rotationRate?.alpha), rotB: round(e.rotationRate?.beta), rotG: round(e.rotationRate?.gamma),
        gx: round(e.accelerationIncludingGravity?.x), gy: round(e.accelerationIncludingGravity?.y), gz: round(e.accelerationIncludingGravity?.z),
        interval: round(e.interval),
      });
    const pointer = (e: PointerEvent) => {
      put('pointer', {
        type: e.type, pointerType: e.pointerType, id: e.pointerId, x: round(e.clientX), y: round(e.clientY),
        mx: round(e.movementX), my: round(e.movementY), pressure: round(e.pressure), tiltX: round(e.tiltX), tiltY: round(e.tiltY),
        twist: round(e.twist), altitude: round((e as PointerEvent & { altitudeAngle?: number }).altitudeAngle),
        azimuth: round((e as PointerEvent & { azimuthAngle?: number }).azimuthAngle), w: round(e.width), h: round(e.height), buttons: e.buttons,
      });
      if (e.type === 'pointerdown') {
        during.current = {};
        // Seed the ranges with where every value is at the start of the pinch.
        for (const [k, v] of Object.entries(live.current)) if (typeof v === 'number') during.current[k] = { min: v, max: v };
      } else if (e.type === 'pointerup' || e.type === 'pointercancel') {
        const range = during.current ?? {};
        during.current = null;
        setLastPinch(
          Object.entries(range)
            .map(([k, r]): [string, number] => [k, Math.round((r.max - r.min) * 10) / 10])
            .filter(([k, d]) => d > 0 && !k.startsWith('pointer.id'))
            .sort((a, b) => b[1] - a[1]),
        );
      }
    };
    const key = (e: KeyboardEvent) => {
      put('key', { key: e.key });
      if (e.key === 'ArrowLeft') onLeave();
    };
    const pads = setInterval(() => {
      const list = navigator.getGamepads?.() ?? [];
      const pads = Array.from(list).filter((p): p is Gamepad => p !== null);
      live.current['gamepads'] = pads.length;
      pads.forEach((p, i) => p.axes.forEach((a, j) => (live.current[`gamepad${i}.axis${j}`] = round(a))));
      redraw((n) => n + 1);
    }, 200);
    window.addEventListener('deviceorientation', orient);
    window.addEventListener('deviceorientationabsolute', orient as EventListener);
    window.addEventListener('devicemotion', motion);
    for (const t of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'pointerrawupdate']) window.addEventListener(t, pointer as EventListener);
    window.addEventListener('keydown', key);
    return () => {
      clearInterval(pads);
      window.removeEventListener('deviceorientation', orient);
      window.removeEventListener('deviceorientationabsolute', orient as EventListener);
      window.removeEventListener('devicemotion', motion);
      for (const t of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'pointerrawupdate']) window.removeEventListener(t, pointer as EventListener);
      window.removeEventListener('keydown', key);
    };
  }, [onLeave]);

  /** iOS-style permission, where it exists; it needs the pinch's user activation. */
  const start = () => {
    setStarted(true);
    type WithPermission = { requestPermission?: () => Promise<string> };
    for (const C of [DeviceOrientationEvent, DeviceMotionEvent] as unknown as WithPermission[]) {
      C.requestPermission?.().then((r) => put('permission', { result: r }), (e) => put('permission', { error: String(e) }));
    }
  };

  const groups = new Map<string, string[]>();
  for (const [k, v] of Object.entries(live.current)) {
    const g = k.split('.')[0];
    groups.set(g, [...(groups.get(g) ?? []), `${k.slice(g.length + 1) || k} ${v}`]);
  }

  return (
    <main className="probe" onPointerDown={() => !started && start()} style={{ touchAction: 'none' }}>
      <h2>Sensors · swipe left to leave</h2>
      <p className="probe-hint">Pinch and move, then rotate your wrist while pinching, then turn your head. After each pinch: what moved most.</p>
      <div className="probe-last">
        <b>Last pinch moved:</b>{' '}
        {lastPinch.length === 0 ? '(pinch once)' : lastPinch.slice(0, 10).map(([k, d]) => `${k} ${d}`).join(' · ')}
      </div>
      {[...groups].map(([g, rows]) => (
        <div key={g} className="probe-row">
          <b>
            {g} ({counts.current[g] ?? 0})
          </b>{' '}
          {rows.join(' · ')}
        </div>
      ))}
      <div className="probe-row">
        <b>APIs</b> {found.current.join(' · ') || 'none matching'}
      </div>
    </main>
  );
}

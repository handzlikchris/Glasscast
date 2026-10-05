// What the server that served this page offers (GET /features): the PC's server offers both, a
// hosted relay only the phone (it can't control any PC). An older server without the endpoint,
// or any error, counts as both, as before.

export interface Features {
  pc: boolean;
  phone: boolean;
}

export const ALL_FEATURES: Features = { pc: true, phone: true };

/** The server's answer, if it makes sense: at least one target, booleans only. */
export function parseFeatures(data: unknown): Features {
  if (typeof data !== 'object' || data === null) return ALL_FEATURES;
  const { pc, phone } = data as Record<string, unknown>;
  if (typeof pc !== 'boolean' || typeof phone !== 'boolean' || (!pc && !phone)) return ALL_FEATURES;
  return { pc, phone };
}

export async function loadFeatures(): Promise<Features> {
  try {
    const response = await fetch('/features', { cache: 'no-store' });
    return response.ok ? parseFeatures(await response.json()) : ALL_FEATURES;
  } catch {
    return ALL_FEATURES;
  }
}

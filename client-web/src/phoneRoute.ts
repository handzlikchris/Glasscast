// Which phone these glasses use on this server: its id there, learnt from the relay
// (`phoneFound`) the first time, after the connect code was typed into the companion. Not a
// secret: it only lets the glasses ask that phone, which still checks them itself (phoneTrust.ts).
// Kept in localStorage next to the pairing; the PC's page and a hosted relay's are different
// origins, so each server has its own.

const PHONE_ID_KEY = 'glassesRemote.phoneId';
const PHONE_ID = /^[A-Za-z0-9_-]{22}$/;

export function loadPhoneId(): string | null {
  try {
    const id = localStorage.getItem(PHONE_ID_KEY);
    return id && PHONE_ID.test(id) ? id : null;
  } catch {
    return null;
  }
}

export function savePhoneId(id: string): void {
  try {
    localStorage.setItem(PHONE_ID_KEY, id);
  } catch {
    // Can't keep it: the next session shows a connect code again.
  }
}

/** The next phone session asks for a connect code again (Pair again: maybe another phone). */
export function forgetPhoneId(): void {
  try {
    localStorage.removeItem(PHONE_ID_KEY);
  } catch {
    // Nothing stored or storage blocked.
  }
}

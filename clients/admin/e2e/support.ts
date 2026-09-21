import { APIRequestContext, Page, expect, request } from '@playwright/test';

/** The API the console talks to. Development profile: https, self-signed. */
export const API = process.env['E2E_API_URL'] ?? 'https://localhost:7139';

/** The phone appsettings.Development.json lists under Auth:AdminPhones. */
export const ADMIN_PHONE = '0315605606';

/** A fresh account every time: ten digits, starting with 9 so it parses as a mobile number. */
export function freshPhone(): string {
  return '9' + Math.floor(Math.random() * 1e9).toString().padStart(9, '0');
}

export interface Session {
  phone: string;
  userId: string;
  accessToken: string;
  expiresAt: string;
  refreshToken: string;
  role: string;
}

/**
 * Sessions minted this run, by phone. One code per account per run: the API caps how many codes
 * a destination may be sent, and the admin phone is used by most tests.
 */
const sessions = new Map<string, Session>();

/**
 * Signs in straight against the API, for test setup. Development's Log sender returns the code in
 * the response, which is the whole reason these tests can run without a phone.
 */
export async function apiSignIn(phone: string): Promise<Session> {
  const cached = sessions.get(phone);
  if (cached) {
    return cached;
  }

  const api = await request.newContext({ baseURL: API, ignoreHTTPSErrors: true });

  const challenge = await api.post('/api/auth/request-otp', { data: { phone } });
  expect(challenge.ok(), await challenge.text()).toBeTruthy();
  const { devCode } = (await challenge.json()) as { devCode: string | null };
  expect(devCode, 'the Development OTP sender exposes the code').toBeTruthy();

  const verified = await api.post('/api/auth/verify-otp', { data: { phone, code: devCode } });
  expect(verified.ok(), await verified.text()).toBeTruthy();
  const result = (await verified.json()) as {
    accessToken: string;
    expiresAt: string;
    refreshToken: string;
    userId: string;
    role: string;
  };

  await api.dispose();

  const session: Session = { phone, ...result };
  sessions.set(phone, session);
  return session;
}

/** An API client that sends this session's bearer token. */
export async function apiAs(session: Session): Promise<APIRequestContext> {
  return request.newContext({
    baseURL: API,
    ignoreHTTPSErrors: true,
    extraHTTPHeaders: { Authorization: `Bearer ${session.accessToken}` },
  });
}

/** Signs in through the login page, reading the development code off the screen. */
export async function signInThroughUi(page: Page, phone: string): Promise<void> {
  await page.goto('/login');
  await page.getByPlaceholder('you@example.com').fill(phone);
  await page.getByRole('button', { name: 'Send code' }).click();

  const code = await page.getByText(/Development code:\s*\d{6}/).textContent();
  const match = /(\d{6})/.exec(code ?? '');
  expect(match, 'the login page shows the development code').toBeTruthy();

  await page.getByPlaceholder('123456').fill(match![1]);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).not.toHaveURL(/\/login/);
}

/**
 * Puts an API session into the browser without going through the login page. The refresh token
 * is single-use and rotates, so the browser gets the pair as minted and the API client keeps
 * using the access token, which is good for an hour.
 */
export async function useSession(page: Page, session: Session): Promise<void> {
  await page.goto('/login');
  await page.evaluate((s) => {
    localStorage.setItem('parknest.token', s.accessToken);
    localStorage.setItem('parknest.expiresAt', s.expiresAt);
    localStorage.setItem('parknest.refreshToken', s.refreshToken);
    localStorage.setItem('parknest.role', s.role);
    localStorage.setItem('parknest.userId', s.userId);
  }, session);
}

/** Bengaluru, near the catalogue centre, so the pin passes the city check. */
export const BENGALURU = { latitude: 12.9716, longitude: 77.5946 };

const ALL_DAY = [0, 1, 2, 3, 4, 5, 6].map((dayOfWeek) => ({
  dayOfWeek,
  startTime: '00:00:00',
  endTime: '00:00:00',
}));

/** A published, always-open, ₹60/hr four-wheeler space owned by `host`. */
export async function publishedSpace(host: Session, title = `E2E space ${Date.now()}`): Promise<string> {
  const api = await apiAs(host);

  const created = await api.post('/api/listings', {
    data: {
      title,
      addressLine: '12 Main Road, Indiranagar',
      city: 'Bengaluru',
      zone: null,
      ...BENGALURU,
      pricePerHour: 60,
      supportedVehicleTypes: ['FourWheeler'],
      availabilityWindows: ALL_DAY,
      timeZoneId: 'Asia/Kolkata',
    },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  const { id } = (await created.json()) as { id: string };

  const published = await api.post(`/api/listings/${id}/publish`);
  expect(published.ok(), await published.text()).toBeTruthy();

  await api.dispose();
  return id;
}

export async function addVehicle(renter: Session, plate = 'KA01AB' + Math.floor(1000 + Math.random() * 9000)): Promise<string> {
  const api = await apiAs(renter);
  const response = await api.post('/api/vehicles', { data: { plateNumber: plate, type: 'FourWheeler' } });
  expect(response.ok(), await response.text()).toBeTruthy();
  const { id } = (await response.json()) as { id: string };
  await api.dispose();
  return id;
}

/** Credits, added by the admin — the support path, so no gateway is involved. */
export async function recharge(admin: Session, userId: string, amount: number): Promise<void> {
  const api = await apiAs(admin);
  const response = await api.post(`/api/wallets/${userId}/recharge`, {
    data: { amount, idempotencyKey: `e2e-${userId}-${Date.now()}` },
  });
  expect(response.ok(), await response.text()).toBeTruthy();
  await api.dispose();
}

/** A booking starting `startInMinutes` from now, for `minutes`. Returns the booking id. */
export async function book(renter: Session, spaceId: string, vehicleId: string, startInMinutes: number, minutes = 60): Promise<string> {
  const api = await apiAs(renter);
  const start = new Date(Date.now() + startInMinutes * 60_000);
  const response = await api.post('/api/bookings', {
    data: {
      parkingSpaceId: spaceId,
      vehicleId,
      startTime: start.toISOString(),
      durationMinutes: minutes,
      idempotencyKey: `e2e-bk-${Date.now()}-${Math.random()}`,
    },
  });
  expect(response.ok(), await response.text()).toBeTruthy();
  const { id } = (await response.json()) as { id: string };
  await api.dispose();
  return id;
}

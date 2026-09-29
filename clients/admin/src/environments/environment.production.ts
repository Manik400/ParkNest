export const environment = {
  production: true,
  // Same-origin in production: the API is expected behind the same host, so no CORS and no
  // hard-coded hostname baked into the bundle.
  apiBaseUrl: '',
  // The Android app, built by .github/workflows/android-apk.yml and published as a GitHub release.
  androidApkUrl: 'https://github.com/Manik400/ParkNest/releases/download/android-latest/parknest.apk',
};

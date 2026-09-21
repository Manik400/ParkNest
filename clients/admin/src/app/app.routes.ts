import { Routes } from '@angular/router';

import { adminGuard, authGuard } from './core/guards';

// Lazy-loaded standalone components: each screen is its own chunk, so the login page does not
// ship the admin console to someone who cannot open it.
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
    title: 'Sign in · ParkNest',
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
        title: 'ParkNest',
      },
      {
        path: 'explore',
        loadComponent: () =>
          import('./features/explore/explore.component').then((m) => m.ExploreComponent),
        title: 'Find parking · ParkNest',
      },
      {
        path: 'spaces/:spaceId',
        loadComponent: () =>
          import('./features/explore/space.component').then((m) => m.SpaceComponent),
        title: 'Space · ParkNest',
      },
      {
        path: 'vehicles',
        loadComponent: () =>
          import('./features/vehicles/vehicles.component').then((m) => m.VehiclesComponent),
        title: 'My vehicles · ParkNest',
      },
      {
        path: 'listings/new',
        loadComponent: () =>
          import('./features/listings/add-listing.component').then((m) => m.AddListingComponent),
        title: 'List a space · ParkNest',
      },
      {
        path: 'listings',
        loadComponent: () =>
          import('./features/listings/listings.component').then((m) => m.ListingsComponent),
        title: 'Listings · ParkNest',
      },
      {
        path: 'bookings',
        loadComponent: () =>
          import('./features/bookings/bookings.component').then((m) => m.BookingsComponent),
        title: 'Bookings · ParkNest',
      },
      {
        path: 'bookings/:bookingId/receipt',
        loadComponent: () =>
          import('./features/bookings/receipt.component').then((m) => m.ReceiptComponent),
        title: 'Receipt · ParkNest',
      },
      {
        path: 'bookings/:bookingId',
        loadComponent: () =>
          import('./features/bookings/booking-detail.component').then(
            (m) => m.BookingDetailComponent,
          ),
        title: 'Booking · ParkNest',
      },
      {
        path: 'wallet',
        loadComponent: () =>
          import('./features/wallet/wallet.component').then((m) => m.WalletComponent),
        title: 'Wallet · ParkNest',
      },
      {
        path: 'profile',
        loadComponent: () =>
          import('./features/profile/profile.component').then((m) => m.ProfileComponent),
        title: 'Profile · ParkNest',
      },
      {
        path: 'disputes',
        loadComponent: () =>
          import('./features/disputes/disputes.component').then((m) => m.DisputesComponent),
        title: 'Disputes · ParkNest',
      },
      {
        path: 'kyc',
        canActivate: [adminGuard],
        loadComponent: () => import('./features/kyc/kyc.component').then((m) => m.KycComponent),
        title: 'Identity checks · ParkNest',
      },
      {
        path: 'payouts',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/payouts/payouts.component').then((m) => m.PayoutsComponent),
        title: 'Payouts · ParkNest',
      },
      {
        path: 'analytics',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/analytics/analytics.component').then((m) => m.AnalyticsComponent),
        title: 'Site activity · ParkNest',
      },
      {
        path: 'pricing',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/pricing/pricing.component').then((m) => m.PricingComponent),
        title: 'Pricing bands · ParkNest',
      },
    ],
  },
  { path: '**', redirectTo: '' },
];

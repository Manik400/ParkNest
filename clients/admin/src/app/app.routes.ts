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
        title: 'Dashboard · ParkNest',
      },
      {
        path: 'explore',
        loadComponent: () =>
          import('./features/explore/explore.component').then((m) => m.ExploreComponent),
        title: 'Find parking - ParkNest',
      },
      {
        path: 'vehicles',
        loadComponent: () =>
          import('./features/vehicles/vehicles.component').then((m) => m.VehiclesComponent),
        title: 'My vehicles - ParkNest',
      },
      {
        path: 'listings/new',
        loadComponent: () =>
          import('./features/listings/add-listing.component').then((m) => m.AddListingComponent),
        title: 'List a space - ParkNest',
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

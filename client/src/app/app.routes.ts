import { type Routes } from '@angular/router';
import { LoginComponent } from '@pages/login/login.component';
import { PostsComponent } from '@pages/posts/posts.component';
import { PostDetailComponent } from '@pages/post-detail/post-detail.component';
import { LibrariesComponent } from '@pages/libraries/libraries.component';
import { SettingsComponent } from '@pages/settings/settings.component';
import { MainLayoutComponent } from '@shared/components/main-layout/main-layout.component';
import { authGuard } from '@services/auth.guard';

import { AppPaths } from './app.paths';

export const routes: Routes = [
    { path: AppPaths.login, component: LoginComponent },
    {
        path: '',
        component: MainLayoutComponent,
        canActivate: [authGuard],
        children: [
            { path: '', pathMatch: 'full', redirectTo: AppPaths.posts },
            { path: 'jobs', loadComponent: () => import('./pages/jobs/jobs.component').then(m => m.JobsPageComponent) },
            { path: AppPaths.logs, loadComponent: () => import('./pages/logs/logs.component').then(m => m.LogsPageComponent) },
            { path: 'duplicates', redirectTo: 'duplicates/exact', pathMatch: 'full' },
            { path: 'duplicates/:tab', loadComponent: () => import('./pages/duplicates/duplicates.component').then(m => m.DuplicatesPageComponent) },
            { path: AppPaths.inbox, redirectTo: `${AppPaths.inbox}/review`, pathMatch: 'full' },
            { path: `${AppPaths.inbox}/:tab`, loadComponent: () => import('./pages/inbox/inbox.component').then(m => m.InboxComponent) },
            { path: AppPaths.posts, component: PostsComponent },
            { path: `${AppPaths.post}/:id`, component: PostDetailComponent },
            { path: AppPaths.libraries, component: LibrariesComponent },
            { path: `${AppPaths.libraries}/:id/${AppPaths.libraryExplorer}`, loadComponent: () => import('./pages/library-explorer/library-explorer.component').then(m => m.LibraryExplorerComponent) },
            { path: AppPaths.tags, redirectTo: `${AppPaths.tags}/tags`, pathMatch: 'full' },
            { path: `${AppPaths.tags}/:tab`, loadComponent: () => import('./pages/tags/tag-management-page.component').then(m => m.TagManagementPageComponent) },
            { path: AppPaths.tagCategories, redirectTo: `${AppPaths.tags}/categories`, pathMatch: 'full' },
            { path: AppPaths.help, loadComponent: () => import('./pages/help/help.component').then(m => m.HelpComponent) },
            { path: AppPaths.stats, loadComponent: () => import('./pages/stats/stats.component').then(m => m.StatsComponent) },
            { path: AppPaths.settings.root, component: SettingsComponent },
            { path: `${AppPaths.settings.root}/:tab`, component: SettingsComponent },
        ]
    },
    { path: '**', redirectTo: '' }
];

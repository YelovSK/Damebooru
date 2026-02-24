import { Routes } from '@angular/router';
import { LoginComponent } from '@pages/login/login.component';
import { PostsComponent } from '@pages/posts/posts.component';
import { PostDetailComponent } from '@pages/post-detail/post-detail.component';
import { BulkTaggingComponent } from '@pages/bulk-tagging/bulk-tagging.component';
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
            { path: 'jobs', loadComponent: () => import('./pages/jobs/jobs.component').then(m => m.JobsPageComponent), data: { pageWidth: 'wide' } },
            { path: AppPaths.logs, loadComponent: () => import('./pages/logs/logs.component').then(m => m.LogsPageComponent), data: { pageWidth: 'wide' } },
            { path: 'duplicates', redirectTo: 'duplicates/groups', pathMatch: 'full' },
            { path: 'duplicates/:tab', loadComponent: () => import('./pages/duplicates/duplicates.component').then(m => m.DuplicatesPageComponent), data: { pageWidth: 'wide' } },
            { path: AppPaths.posts, component: PostsComponent, data: { pageWidth: 'full' } },
            { path: `${AppPaths.post}/:id`, component: PostDetailComponent, data: { pageWidth: 'full' } },
            { path: AppPaths.bulkTagging, component: BulkTaggingComponent, data: { pageWidth: 'full' } },
            { path: AppPaths.libraries, component: LibrariesComponent, data: { pageWidth: 'content' } },
            { path: `${AppPaths.libraries}/:id/${AppPaths.libraryExplorer}`, loadComponent: () => import('./pages/library-explorer/library-explorer.component').then(m => m.LibraryExplorerComponent), data: { pageWidth: 'full' } },
            { path: AppPaths.tags, redirectTo: `${AppPaths.tags}/tags`, pathMatch: 'full' },
            { path: `${AppPaths.tags}/:tab`, loadComponent: () => import('./pages/tags/tag-management-page.component').then(m => m.TagManagementPageComponent), data: { pageWidth: 'wide' } },
            { path: AppPaths.tagCategories, redirectTo: `${AppPaths.tags}/categories`, pathMatch: 'full' },
            { path: AppPaths.help, loadComponent: () => import('./pages/help/help.component').then(m => m.HelpComponent), data: { pageWidth: 'content' } },
            { path: AppPaths.info, loadComponent: () => import('@pages/info/info.component').then(m => m.InfoComponent), data: { pageWidth: 'content' } },
            { path: AppPaths.settings.root, component: SettingsComponent, data: { pageWidth: 'content' } },
            { path: `${AppPaths.settings.root}/:tab`, component: SettingsComponent, data: { pageWidth: 'content' } },
        ]
    },
    { path: '**', redirectTo: '' }
];

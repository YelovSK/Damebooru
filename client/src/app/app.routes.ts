import { Routes } from '@angular/router';
import { HomeComponent } from '@pages/home/home.component';
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
    { path: AppPaths.home, component: HomeComponent },
    { path: AppPaths.login, component: LoginComponent },
    {
        path: '',
        component: MainLayoutComponent,
        canActivate: [authGuard],
        children: [
            { path: 'jobs', loadComponent: () => import('./pages/jobs/jobs.component').then(m => m.JobsPageComponent) },
            { path: 'duplicates', loadComponent: () => import('./pages/duplicates/duplicates.component').then(m => m.DuplicatesPageComponent) },
            { path: AppPaths.posts, component: PostsComponent },
            { path: `${AppPaths.post}/:id`, component: PostDetailComponent },
            { path: AppPaths.bulkTagging, component: BulkTaggingComponent },
            { path: AppPaths.libraries, component: LibrariesComponent },
            { path: AppPaths.tags, loadComponent: () => import('./pages/tags/tag-management-page.component').then(m => m.TagManagementPageComponent) },
            { path: `${AppPaths.tags}/:tab`, loadComponent: () => import('./pages/tags/tag-management-page.component').then(m => m.TagManagementPageComponent) },
            { path: AppPaths.tagCategories, redirectTo: `${AppPaths.tags}/categories`, pathMatch: 'full' },
            { path: AppPaths.help, loadComponent: () => import('./pages/help/help.component').then(m => m.HelpComponent) },
            { path: AppPaths.info, loadComponent: () => import('@pages/info/info.component').then(m => m.InfoComponent) },
            { path: AppPaths.settings.root, component: SettingsComponent },
            { path: `${AppPaths.settings.root}/:tab`, component: SettingsComponent },
        ]
    },
    { path: '**', redirectTo: '' }
];

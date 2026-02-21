export const AppPaths = {
    login: 'login',
    posts: 'posts',
    post: 'post',
    bulkTagging: 'bulk-tagging',
    libraries: 'libraries',
    tags: 'tags',
    help: 'help',
    tagCategories: 'tag-categories',
    info: 'info',
    settings: {
        root: 'settings',
        autoTagging: 'auto-tagging'
    },
    jobs: 'jobs',
    duplicates: 'duplicates'
} as const;

// Helper to build array commands for Router.navigate or [routerLink]
export const AppLinks = {
    login: () => ['/', AppPaths.login],
    posts: () => ['/', AppPaths.posts],
    post: (id: string | number) => ['/', AppPaths.post, id],
    bulkTagging: () => ['/', AppPaths.bulkTagging],
    libraries: () => ['/', AppPaths.libraries],
    tags: () => ['/', AppPaths.tags],
    help: () => ['/', AppPaths.help],
    tagCategories: () => ['/', AppPaths.tags, 'categories'],
    info: () => ['/', AppPaths.info],
    settings: () => ['/', AppPaths.settings.root],
    settingsAutoTagging: () => ['/', AppPaths.settings.root, AppPaths.settings.autoTagging],
    jobs: () => ['/', AppPaths.jobs],
    duplicates: () => ['/', AppPaths.duplicates],
};

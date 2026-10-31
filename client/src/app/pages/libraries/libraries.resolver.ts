import { inject } from '@angular/core';
import { type ResolveFn } from '@angular/router';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type Library } from '@services/api/damebooru/models';

export const librariesResolver: ResolveFn<Library[]> = () => inject(DamebooruService).getLibraries();

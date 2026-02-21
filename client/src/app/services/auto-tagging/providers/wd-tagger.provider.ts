import { Validators } from '@angular/forms';
import { Observable, map, catchError, of } from 'rxjs';
import {
    AutoTaggingResult,
    ProviderSetting,
    TaggingProvider,
    SettingValue,
    CategorizedTag,
} from '../models';
import { WdTaggerService, WdTaggerResponse } from '../../api/wd-tagger/wd-tagger.service';
import { Safety } from '@app/services/api/damebooru/models';

interface WdTaggerSettings extends Record<string, SettingValue> {
    minConfidence: number;
}

/**
 * Tagging provider using WD Tagger (wd-hydrus-tagger or similar).
 * Runs locally via Docker container and provides high-quality anime tag predictions.
 */
export class WdTaggerProvider implements TaggingProvider {
    readonly id = 'wd-tagger-provider';
    readonly name = 'WD Tagger';
    readonly priority = 20; // Higher priority than SauceNAO since it's local and fast
    readonly defaultEnabled = true;

    private enabled = true;
    private settings: WdTaggerSettings = {
        minConfidence: 0.5,
    };

    private readonly settingsSchema: ProviderSetting[] = [
        {
            key: 'minConfidence',
            label: 'Minimum Confidence',
            type: 'number',
            description: 'Minimum confidence threshold for tags (0.0 to 1.0)',
            defaultValue: 0.5,
            validators: [Validators.min(0), Validators.max(1)],
        },
    ];

    constructor(private wdTagger: WdTaggerService) { }

    getSettingsSchema(): ProviderSetting[] {
        return this.settingsSchema;
    }

    getSettings(): WdTaggerSettings {
        return { ...this.settings };
    }

    updateSettings(settings: WdTaggerSettings): void {
        this.settings = { ...this.settings, ...settings };
    }

    isEnabled(): boolean {
        return this.enabled;
    }

    setEnabled(enabled: boolean): void {
        this.enabled = enabled;
    }

    canHandle(file: File): boolean {
        // Can handle any image file
        return file.type.startsWith('image/');
    }

    tag(file: File): Observable<AutoTaggingResult> {
        return this.wdTagger.predict(file).pipe(
            map(response => this.parseResponse(response)),
            catchError(error => {
                console.error('WD Tagger error:', error);
                return of(this.emptyResult());
            }),
        );
    }

    private parseResponse(response: WdTaggerResponse): AutoTaggingResult {
        const minConf = this.settings.minConfidence;
        const tags: CategorizedTag[] = [];

        // Add general tags
        for (const [tag, confidence] of Object.entries(response.general)) {
            if (confidence >= minConf) {
                tags.push({ name: tag, category: 'general' });
            }
        }

        // Add character tags
        for (const [tag, confidence] of Object.entries(response.characters)) {
            if (confidence >= minConf) {
                tags.push({ name: tag, category: 'character' });
            }
        }

        // Determine safety from ratings
        const safety = this.determineSafety(response.ratings);

        // Calculate overall confidence as the max rating confidence
        const maxRating = Math.max(
            response.ratings.general,
            response.ratings.sensitive,
            response.ratings.questionable,
            response.ratings.explicit,
        );

        return {
            categorizedTags: tags,
            safety,
            confidence: maxRating,
            provider: this.name,
            providerId: this.id,
        };
    }

    private determineSafety(ratings: WdTaggerResponse['ratings']): Safety {
        const { general, sensitive, questionable, explicit: explicitRating } = ratings;

        // Find the highest rated category
        const max = Math.max(general, sensitive, questionable, explicitRating);

        if (max === explicitRating) {
            return 'unsafe';
        }
        if (max === sensitive || max === questionable) {
            return 'sketchy';
        }
        return 'safe';
    }

    private emptyResult(): AutoTaggingResult {
        return {
            categorizedTags: [],
            provider: this.name,
            providerId: this.id,
        };
    }
}

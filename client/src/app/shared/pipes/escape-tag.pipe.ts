import { Pipe, type PipeTransform } from "@angular/core";
import { escapeTagName } from "@shared/utils/utils";

@Pipe({
    name: 'tag',
    standalone: true,
})
export class TagPipe implements PipeTransform {
    transform(value: string): string {
        return escapeTagName(value);
    }
}
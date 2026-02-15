import { CollectionViewer, DataSource } from '@angular/cdk/collections';
import { BehaviorSubject, Observable } from 'rxjs';

export class VirtualRowIndexDataSource extends DataSource<number> {
    private readonly rowsSubject = new BehaviorSubject<readonly number[]>([]);

    private buffer = new Uint32Array(0);
    private length = 0;

    connect(_collectionViewer: CollectionViewer): Observable<readonly number[]> {
        return this.rowsSubject.asObservable();
    }

    disconnect(): void {
        this.rowsSubject.complete();
    }

    setLength(length: number): void {
        const normalizedLength = Math.max(0, Math.floor(length));
        if (this.length === normalizedLength) {
            return;
        }

        this.ensureCapacity(normalizedLength);
        this.length = normalizedLength;

        const view = this.buffer.subarray(0, normalizedLength) as unknown as readonly number[];
        this.rowsSubject.next(view);
    }

    private ensureCapacity(requiredLength: number): void {
        if (requiredLength <= this.buffer.length) {
            return;
        }

        let nextCapacity = Math.max(64, this.buffer.length);
        while (nextCapacity < requiredLength) {
            nextCapacity *= 2;
        }

        const nextBuffer = new Uint32Array(nextCapacity);
        nextBuffer.set(this.buffer);

        for (let index = this.buffer.length; index < nextCapacity; index += 1) {
            nextBuffer[index] = index;
        }

        this.buffer = nextBuffer;
    }
}

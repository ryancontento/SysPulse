// Pointer tracking for the Flight Recorder scope. Reports the pointer's horizontal position as a 0–1 fraction
// of the element's width, at most once per animation frame.
window.sysPulseScope = {
    attach(element, dotNet) {
        let frame = 0;
        let pending = null;

        const fraction = e => {
            const rect = element.getBoundingClientRect();
            return Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
        };

        const queue = (kind, value) => {
            pending = { kind, value };
            if (!frame) {
                frame = requestAnimationFrame(() => {
                    frame = 0;
                    dotNet.invokeMethodAsync("OnPointer", pending.kind, pending.value);
                });
            }
        };

        const handlers = {
            pointermove: e => queue("move", fraction(e)),
            pointerleave: () => queue("leave", -1),
            click: e => queue("click", fraction(e)),
        };

        for (const [name, handler] of Object.entries(handlers))
            element.addEventListener(name, handler);

        element._sysPulseScope = () => {
            cancelAnimationFrame(frame);
            for (const [name, handler] of Object.entries(handlers))
                element.removeEventListener(name, handler);
        };
    },

    detach(element) {
        element?._sysPulseScope?.();
    },
};

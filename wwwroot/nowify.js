const fallback = { background: "#301934", text: "#ffffff" };

function luminance(colour) {
    const channels = colour.map(value => {
        const channel = value / 255;
        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
    });
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
}

export async function extractPalette(imageUrl) {
    if (!imageUrl) return fallback;

    try {
        const image = new Image();
        image.crossOrigin = "anonymous";
        const loaded = await new Promise(resolve => {
            const timeout = setTimeout(() => finish(false), 10000);
            const finish = success => {
                clearTimeout(timeout);
                image.onload = null;
                image.onerror = null;
                resolve(success);
            };
            image.onload = () => finish(true);
            image.onerror = () => finish(false);
            image.src = imageUrl;
        });
        if (!loaded) return fallback;

        const canvas = document.createElement("canvas");
        canvas.width = 40;
        canvas.height = 40;
        const context = canvas.getContext("2d", { willReadFrequently: true });
        if (!context) return fallback;
        context.drawImage(image, 0, 0, 40, 40);
        const pixels = context.getImageData(0, 0, 40, 40).data;
        const buckets = new Map();

        for (let index = 0; index < pixels.length; index += 4) {
            if (pixels[index + 3] < 128) continue;
            const colour = [pixels[index], pixels[index + 1], pixels[index + 2]];
            const key = colour.map(channel => channel >> 4).join(",");
            const bucket = buckets.get(key) ?? { count: 0, sum: [0, 0, 0] };
            bucket.count++;
            colour.forEach((channel, offset) => bucket.sum[offset] += channel);
            buckets.set(key, bucket);
        }

        let best;
        let bestScore = -1;
        for (const bucket of buckets.values()) {
            const colour = bucket.sum.map(channel => Math.round(channel / bucket.count));
            const max = Math.max(...colour) / 255;
            const min = Math.min(...colour) / 255;
            const saturation = max === 0 ? 0 : (max - min) / max;
            // Prefer vibrant midtones, while retaining neutral album art as a fallback.
            const score = Math.sqrt(bucket.count) * (0.1 + saturation)
                * (0.25 + Math.sin(Math.PI * (max + min) / 2));
            if (score > bestScore) {
                bestScore = score;
                best = colour;
            }
        }
        if (!best) return fallback;

        const light = luminance(best);
        const blackContrast = (light + 0.05) / 0.05;
        const whiteContrast = 1.05 / (light + 0.05);
        return {
            background: `#${best.map(channel => channel.toString(16).padStart(2, "0")).join("")}`,
            text: blackContrast >= whiteContrast ? "#000000" : "#ffffff"
        };
    } catch {
        // CORS restrictions, invalid images, and unavailable Canvas all use the default palette.
        return fallback;
    }
}

/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "A Gaussian blur in two passes, across then down; radius in pixels of the picture.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Blur"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "radius", "TYPE": "float", "LABEL": "Radius (px)", "DEFAULT": 4.0, "MIN": 0.0, "MAX": 32.0, "IDENTITY": 0.0 }
    ],
    "PASSES": [
        { "TARGET": "across" },
        { }
    ]
}*/
vec4 pm_tap(vec2 p) {
    return PASSINDEX == 0 ? IMG_PIXEL(inputImage, p) : IMG_PIXEL(across, p);
}

void main() {
    float r = clamp(radius, 0.0, 32.0);
    if (r < 0.5) {
        gl_FragColor = pm_tap(gl_FragCoord.xy);
        return;
    }
    float sigma = max(r / 2.0, 0.5);
    vec2 dir = PASSINDEX == 0 ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 sum = vec4(0.0);
    float total = 0.0;
    for (int i = -32; i <= 32; i++) {
        float fi = float(i);
        if (abs(fi) > r)
            continue;
        float k = exp(-0.5 * fi * fi / (sigma * sigma));
        sum += k * pm_tap(gl_FragCoord.xy + dir * fi);
        total += k;
    }
    gl_FragColor = sum / total;
}

/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Turns the hue, scales the saturation and lifts or lowers the lightness.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Colour"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "hue", "TYPE": "float", "LABEL": "Hue (turns)", "DEFAULT": 0.0, "MIN": -0.5, "MAX": 0.5 },
        { "NAME": "saturation", "TYPE": "float", "LABEL": "Saturation", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 },
        { "NAME": "lightness", "TYPE": "float", "LABEL": "Lightness", "DEFAULT": 0.0, "MIN": -1.0, "MAX": 1.0 }
    ]
}*/
vec3 pm_rgb2hsv(vec3 c) {
    vec4 k = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, k.wz), vec4(c.gb, k.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1.0e-10)), d / (q.x + 1.0e-10), q.x);
}

vec3 pm_hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void main() {
    vec4 c = IMG_THIS_PIXEL(inputImage);
    vec3 hsv = pm_rgb2hsv(c.rgb);
    hsv.x = fract(hsv.x + hue);
    hsv.y = clamp(hsv.y * saturation, 0.0, 1.0);
    vec3 rgb = pm_hsv2rgb(hsv);
    rgb = lightness >= 0.0 ? mix(rgb, vec3(1.0), lightness) : rgb * (1.0 + lightness);
    gl_FragColor = vec4(rgb, c.a);
}

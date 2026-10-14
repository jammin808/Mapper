/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Darkens toward the corners: amount, where the darkening starts (size, from the centre) and how soft its edge is.",
    "CREDIT": "PlayMap",
    "CATEGORIES": ["Look"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "amount", "TYPE": "float", "LABEL": "Amount", "DEFAULT": 0.6, "MIN": 0.0, "MAX": 1.0, "IDENTITY": 0.0 },
        { "NAME": "size", "TYPE": "float", "LABEL": "Size", "DEFAULT": 0.35, "MIN": 0.0, "MAX": 1.0 },
        { "NAME": "softness", "TYPE": "float", "LABEL": "Softness", "DEFAULT": 0.4, "MIN": 0.01, "MAX": 1.0 }
    ]
}*/
void main() {
    vec4 c = IMG_THIS_PIXEL(inputImage);
    vec2 d = isf_FragNormCoord - vec2(0.5);
    d.x *= RENDERSIZE.x / max(RENDERSIZE.y, 1.0);
    float r = length(d);
    float v = 1.0 - amount * smoothstep(size, size + softness, r);
    gl_FragColor = vec4(c.rgb * v, c.a);
}

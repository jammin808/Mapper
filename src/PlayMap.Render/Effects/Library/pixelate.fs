/*{
    "ISFVSN": "2",
    "VSN": "1.0",
    "DESCRIPTION": "Square cells of one colour each, the colour at the cell's centre; size in pixels.",
    "CREDIT": "PlayMap (MIT)",
    "CATEGORIES": ["Look"],
    "INPUTS": [
        { "NAME": "inputImage", "TYPE": "image" },
        { "NAME": "size", "TYPE": "float", "LABEL": "Cell (px)", "DEFAULT": 8.0, "MIN": 1.0, "MAX": 256.0, "IDENTITY": 1.0 }
    ]
}*/
void main() {
    float s = max(floor(size), 1.0);
    vec2 cell = floor(gl_FragCoord.xy / s);
    vec2 centre = min((cell + 0.5) * s, RENDERSIZE - vec2(0.5));
    gl_FragColor = IMG_PIXEL(inputImage, centre);
}

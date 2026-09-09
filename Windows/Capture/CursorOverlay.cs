namespace Windows.Capture;

public static class CursorOverlay
{
    private static readonly byte[,] WindowsDarkCursorMap = new byte[19, 12]
    {
        { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 1, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 1, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 1, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0 },
        { 1, 2, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0 },
        { 1, 2, 2, 2, 2, 2, 1, 0, 0, 0, 0, 0 },
        { 1, 2, 2, 2, 2, 2, 2, 1, 0, 0, 0, 0 },
        { 1, 2, 2, 2, 2, 2, 2, 2, 1, 0, 0, 0 },
        { 1, 2, 2, 2, 2, 2, 2, 2, 2, 1, 0, 0 },
        { 1, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 0 },
        { 1, 2, 2, 1, 2, 2, 1, 0, 0, 0, 0, 0 },
        { 1, 2, 1, 0, 1, 2, 2, 1, 0, 0, 0, 0 },
        { 1, 1, 0, 0, 1, 2, 2, 1, 0, 0, 0, 0 },
        { 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0 },
        { 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0 }
    };

    public static unsafe void Apply(
        byte* pFrame,
        uint frameRowPitch,
        int frameWidth,
        int frameHeight,
        int cursorX,
        int cursorY)
    {
        int mapHeight = WindowsDarkCursorMap.GetLength(0);
        int mapWidth = WindowsDarkCursorMap.GetLength(1);

        for (int y = 0; y < mapHeight; y++)
        {
            int targetY = cursorY + y;
            if (targetY < 0 || targetY >= frameHeight) continue;

            byte* pFrameRow = pFrame + (targetY * frameRowPitch);

            for (int x = 0; x < mapWidth; x++)
            {
                int targetX = cursorX + x;
                if (targetX < 0 || targetX >= frameWidth) continue;

                byte pixelType = WindowsDarkCursorMap[y, x];
                if (pixelType == 0) continue;

                byte* pPixel = pFrameRow + (targetX * 4);

                if (pixelType == 1) // Borda Branca
                {
                    pPixel[0] = 255; pPixel[1] = 255; pPixel[2] = 255; pPixel[3] = 255;
                }
                else if (pixelType == 2) // Preenchimento Preto
                {
                    pPixel[0] = 0; pPixel[1] = 0; pPixel[2] = 0; pPixel[3] = 255;
                }
            }
        }
    }
}
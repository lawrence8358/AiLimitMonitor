using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Core.Tests;

public class PetBorderTests
{
    [Fact]
    public void Wrap_draws_box_with_inner_padding_and_pet_at_start()
    {
        var wrapped = PetBorder.Wrap("ab\nc\n", tick: 0);

        var lines = wrapped.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "🐹───┐",   // the two-cell pet replaces the top-left corner and one dash
            "│ ab │",
            "│ c  │",
            "└────┘",
        ], lines);
    }

    [Fact]
    public void Wrap_moves_pet_clockwise_with_ticks()
    {
        var lines = PetBorder.Wrap("ab", tick: 2).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("┌─🐹─┐", lines[0]);
    }

    [Fact]
    public void Wrap_keeps_alignment_on_the_vertical_edges()
    {
        // Right edge: the pet replaces the padding space and the border bar.
        var right = PetBorder.Wrap("ab", tick: 6).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("│ ab🐹", right[1]);

        // Left edge: the pet replaces the border bar and the padding space.
        var left = PetBorder.Wrap("ab", tick: 13).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("🐹ab │", left[1]);
    }

    [Fact]
    public void Wrap_keeps_the_border_when_the_pet_is_hidden()
    {
        var wrapped = PetBorder.Wrap("ab\nc\n", tick: 7, showPet: false);

        var lines = wrapped.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "┌────┐",
            "│ ab │",
            "│ c  │",
            "└────┘",
        ], lines);
        Assert.DoesNotContain(PetBorder.Pet, wrapped);
    }

    [Fact]
    public void Wrap_pads_ragged_lines_to_widest()
    {
        var lines = PetBorder.Wrap("wide line\nx", tick: 5, showPet: false)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, l => Assert.Equal(lines[0].Length, l.Length));
    }

    [Theory]
    [InlineData(0, 0, 0)]                  // top-left corner
    [InlineData(5, 5, 0)]                  // end of top edge
    [InlineData(6, 5, 1)]                  // first step down the right edge
    [InlineData(7, 5, 2)]                  // bottom-right corner
    [InlineData(12, 0, 2)]                 // bottom-left corner
    [InlineData(13, 0, 1)]                 // climbing the left edge
    public void PetPosition_walks_the_border_clockwise(long tick, int x, int y) =>
        Assert.Equal((x, y), PetBorder.PetPosition(totalWidth: 6, totalHeight: 3, tick));

    [Fact]
    public void PetPosition_wraps_around_the_perimeter()
    {
        var perimeter = PetBorder.PerimeterLength(6, 3);

        Assert.Equal((0, 0), PetBorder.PetPosition(6, 3, perimeter));
        Assert.Equal(14, perimeter);
    }
}

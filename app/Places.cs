namespace Imago;

/// <summary>Offline nearest-city lookup, so no coordinates ever leave the PC for a place name.</summary>
internal static class Places
{
    private static readonly (string Name, double Lat, double Lon)[] Cities =
    [
        // India
        ("Mumbai", 19.0760, 72.8777), ("New Delhi", 28.6139, 77.2090), ("Bengaluru", 12.9716, 77.5946),
        ("Hyderabad", 17.3850, 78.4867), ("Chennai", 13.0827, 80.2707), ("Kolkata", 22.5726, 88.3639),
        ("Pune", 18.5204, 73.8567), ("Ahmedabad", 23.0225, 72.5714), ("Gandhinagar", 23.2156, 72.6369),
        ("Jaipur", 26.9124, 75.7873), ("Surat", 21.1702, 72.8311), ("Lucknow", 26.8467, 80.9462),
        ("Kanpur", 26.4499, 80.3319), ("Nagpur", 21.1458, 79.0882), ("Indore", 22.7196, 75.8577),
        ("Bhopal", 23.2599, 77.4126), ("Patna", 25.5941, 85.1376), ("Vadodara", 22.3072, 73.1812),
        ("Ludhiana", 30.9010, 75.8573), ("Agra", 27.1767, 78.0081), ("Nashik", 19.9975, 73.7898),
        ("Varanasi", 25.3176, 82.9739), ("Srinagar", 34.0837, 74.7973), ("Amritsar", 31.6340, 74.8723),
        ("Chandigarh", 30.7333, 76.7794), ("Guwahati", 26.1445, 91.7362), ("Bhubaneswar", 20.2961, 85.8245),
        ("Thiruvananthapuram", 8.5241, 76.9366), ("Kochi", 9.9312, 76.2673), ("Coimbatore", 11.0168, 76.9558),
        ("Madurai", 9.9252, 78.1198), ("Visakhapatnam", 17.6868, 83.2185), ("Vijayawada", 16.5062, 80.6480),
        ("Mysuru", 12.2958, 76.6394), ("Mangaluru", 12.9141, 74.8560), ("Panaji", 15.4909, 73.8278),
        ("Dehradun", 30.3165, 78.0322), ("Shimla", 31.1048, 77.1734), ("Ranchi", 23.3441, 85.3096),
        ("Raipur", 21.2514, 81.6296), ("Jodhpur", 26.2389, 73.0243), ("Udaipur", 24.5854, 73.7125),
        ("Rajkot", 22.3039, 70.8022), ("Gurugram", 28.4595, 77.0266), ("Noida", 28.5355, 77.3910),
        ("Imphal", 24.8170, 93.9368), ("Shillong", 25.5788, 91.8933), ("Jammu", 32.7266, 74.8570),
        ("Puducherry", 11.9416, 79.8083), ("Kozhikode", 11.2588, 75.7804), ("Jamshedpur", 22.8046, 86.2029),
        ("Prayagraj", 25.4358, 81.8463), ("Gwalior", 26.2183, 78.1828), ("Jabalpur", 23.1815, 79.9864),
        ("Aurangabad", 19.8762, 75.3433), ("Hubballi", 15.3647, 75.1240), ("Tiruchirappalli", 10.7905, 78.7047),
        ("Port Blair", 11.6234, 92.7265), ("Leh", 34.1526, 77.5771), ("Gangtok", 27.3389, 88.6065),
        ("Kohima", 25.6751, 94.1086), ("Agartala", 23.8315, 91.2868), ("Aizawl", 23.7271, 92.7176),
        ("Itanagar", 27.0844, 93.6053),
        // South Asia
        ("Karachi", 24.8607, 67.0011), ("Lahore", 31.5204, 74.3587), ("Islamabad", 33.6844, 73.0479),
        ("Dhaka", 23.8103, 90.4125), ("Kathmandu", 27.7172, 85.3240), ("Colombo", 6.9271, 79.8612),
        ("Thimphu", 27.4728, 89.6390), ("Male", 4.1755, 73.5093), ("Kabul", 34.5553, 69.2075),
        // Asia Pacific
        ("Bangkok", 13.7563, 100.5018), ("Singapore", 1.3521, 103.8198), ("Kuala Lumpur", 3.1390, 101.6869),
        ("Jakarta", -6.2088, 106.8456), ("Manila", 14.5995, 120.9842), ("Hong Kong", 22.3193, 114.1694),
        ("Shanghai", 31.2304, 121.4737), ("Beijing", 39.9042, 116.4074), ("Shenzhen", 22.5431, 114.0579),
        ("Seoul", 37.5665, 126.9780), ("Tokyo", 35.6762, 139.6503), ("Taipei", 25.0330, 121.5654),
        ("Hanoi", 21.0278, 105.8342), ("Ho Chi Minh City", 10.8231, 106.6297), ("Sydney", -33.8688, 151.2093),
        ("Melbourne", -37.8136, 144.9631), ("Auckland", -36.8485, 174.7633),
        // Middle East and Africa
        ("Dubai", 25.2048, 55.2708), ("Abu Dhabi", 24.4539, 54.3773), ("Doha", 25.2854, 51.5310),
        ("Riyadh", 24.7136, 46.6753), ("Tehran", 35.6892, 51.3890), ("Tel Aviv", 32.0853, 34.7818),
        ("Istanbul", 41.0082, 28.9784), ("Cairo", 30.0444, 31.2357), ("Lagos", 6.5244, 3.3792),
        ("Nairobi", -1.2921, 36.8219), ("Johannesburg", -26.2041, 28.0473),
        // Europe
        ("London", 51.5074, -0.1278), ("Dublin", 53.3498, -6.2603), ("Paris", 48.8566, 2.3522),
        ("Amsterdam", 52.3676, 4.9041), ("Berlin", 52.5200, 13.4050), ("Zurich", 47.3769, 8.5417),
        ("Madrid", 40.4168, -3.7038), ("Lisbon", 38.7223, -9.1393), ("Rome", 41.9028, 12.4964),
        ("Stockholm", 59.3293, 18.0686), ("Helsinki", 60.1699, 24.9384), ("Warsaw", 52.2297, 21.0122),
        ("Moscow", 55.7558, 37.6173),
        // Americas
        ("New York", 40.7128, -74.0060), ("Boston", 42.3601, -71.0589), ("Washington", 38.9072, -77.0369),
        ("Chicago", 41.8781, -87.6298), ("Austin", 30.2672, -97.7431), ("Seattle", 47.6062, -122.3321),
        ("San Francisco", 37.7749, -122.4194), ("Los Angeles", 34.0522, -118.2437), ("Toronto", 43.6532, -79.3832),
        ("Vancouver", 49.2827, -123.1207), ("Mexico City", 19.4326, -99.1332), ("Sao Paulo", -23.5505, -46.6333),
        ("Buenos Aires", -34.6037, -58.3816),
    ];

    /// <summary>"Pune" when within 25 km, "Near Pune" within 150 km, else empty.</summary>
    public static string Nearest(double lat, double lon)
    {
        string best = ""; double bestKm = double.MaxValue;
        foreach (var c in Cities)
        {
            double km = DistanceKm(lat, lon, c.Lat, c.Lon);
            if (km < bestKm) { bestKm = km; best = c.Name; }
        }
        return bestKm <= 25 ? best : bestKm <= 150 ? "Near " + best : "";
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0088;
        double dLat = Rad(lat2 - lat1), dLon = Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        static double Rad(double d) => d * Math.PI / 180;
    }
}
